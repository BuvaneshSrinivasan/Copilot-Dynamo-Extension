// DO NOT DELETE, SAVED FOR FUTURE USE — server-side Gemini streaming for the subscription model.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DynamoCopilot.Server.Models;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// GeminiService — Streams responses from the Google Gemini API
// =============================================================================
//
// Gemini streaming endpoint:
//   POST https://generativelanguage.googleapis.com/v1beta/models/{model}
//        :streamGenerateContent?alt=sse&key={apiKey}
//
// The "?alt=sse" parameter tells Gemini to respond with Server-Sent Events.
// The response is a stream of lines. Each line that carries data starts with "data: ":
//
//   data: {"candidates":[{"content":{"parts":[{"text":"Hello"}],"role":"model"}}],...}
//   data: {"candidates":[{"content":{"parts":[{"text":" there!"}],"role":"model"},
//          "finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,
//          "candidatesTokenCount":2,"totalTokenCount":12}}
//
// The LAST chunk always contains "finishReason":"STOP" and the full usageMetadata.
// We capture totalTokenCount from that chunk and write it into UsageTracker so
// RateLimitMiddleware can update the user's daily token count after the stream ends.
// =============================================================================

public class GeminiService : ILlmService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UsageTracker _usageTracker;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;

    // IConfiguration is ASP.NET Core's abstraction over ALL config sources:
    // appsettings.json, appsettings.Development.json, env vars, user secrets.
    // The framework injects it automatically — just declare it in the constructor.
    public GeminiService(IHttpClientFactory httpClientFactory, IConfiguration configuration, UsageTracker usageTracker)
    {
        _httpClientFactory = httpClientFactory;
        _usageTracker = usageTracker;

        // The "?? throw" pattern: read config value or crash at startup with a clear message.
        // Much better than a NullReferenceException thrown mid-request with no context.
        _apiKey = configuration["Gemini:ApiKey"] is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException(
                "Gemini:ApiKey is not configured. " +
                "Run: dotnet user-secrets set \"Gemini:ApiKey\" \"YOUR_KEY\" " +
                "or set the GEMINI__APIKEY environment variable in Railway.");

        _model = configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        // Use the config-provided system prompt if it has content; otherwise use the built-in one.
        _systemPrompt = configuration["Gemini:SystemPrompt"] is { Length: > 0 } sp
            ? sp
            : DefaultSystemPrompt;
    }

    // [EnumeratorCancellation] is required when a CancellationToken is used inside
    // an async iterator method (a method that uses both 'async' and 'yield return').
    // It connects the token you pass to GetAsyncEnumerator() to the method body.
    // Without it, the cancellation token isn't wired up correctly — the method
    // would keep running even after the caller cancels.
    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ── 1. BUILD THE REQUEST BODY ──────────────────────────────────────────
        // Gemini uses "model" for the assistant role (OpenAI uses "assistant").
        // We handle that mapping here so callers don't need to know Gemini's quirks.
        // We also filter out any "system" role messages — Gemini handles those
        // separately via the system_instruction field below.
        var contents = messages
            .Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? "model"
                    : "user",
                parts = new[] { new { text = m.Content } }
            })
            .ToArray();

        var requestBody = new
        {
            // system_instruction is Gemini's equivalent of the OpenAI system message.
            // It sets the AI's persona and rules for the entire conversation.
            system_instruction = new
            {
                parts = new[] { new { text = _systemPrompt } }
            },
            contents,
            // Disable thinking tokens. Gemini 2.5 Flash enables thinking by default,
            // which bills at $3.50/1M tokens — ~6x more expensive than regular output.
            // Code generation does not benefit from thinking; disabling it cuts costs
            // by ~85% with no meaningful quality loss for this use case.
            generation_config = new
            {
                thinking_config = new { thinking_budget = 0 }
            }
        };

        // JsonNamingPolicy.SnakeCaseLower converts C# PascalCase property names
        // to snake_case automatically (e.g. SystemInstruction → system_instruction).
        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}" +
                  $":streamGenerateContent?alt=sse&key={_apiKey}";

        // ── 2. SEND THE REQUEST ────────────────────────────────────────────────
        var httpClient = _httpClientFactory.CreateClient();

        // HttpCompletionOption.ResponseHeadersRead is CRITICAL for streaming.
        // Default behaviour: HttpClient downloads the ENTIRE response body before returning.
        // With ResponseHeadersRead: HttpClient returns as soon as response headers arrive,
        // giving us a stream we can read token-by-token as Gemini sends them.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Gemini API returned {(int)response.StatusCode}: {errorBody}");
        }

        // ── 3. READ AND PARSE THE SSE STREAM ──────────────────────────────────
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            // SSE format rules:
            //   - Lines starting with "data: " carry the payload
            //   - Blank lines are message separators — skip them
            //   - Lines starting with ":" are comments — skip them
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            // Slice off the "data: " prefix (6 characters)
            var data = line["data: ".Length..];

            // Safely parse JSON — skip any malformed lines rather than crashing
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (doc)
            {
                // Navigate the JSON path: root → candidates[0] → content → parts[0] → text
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates)) continue;
                if (candidates.GetArrayLength() == 0) continue;

                var candidate = candidates[0];
                if (!candidate.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                if (parts.GetArrayLength() == 0) continue;

                // TryGetProperty is safer than GetProperty — GetProperty throws KeyNotFoundException
                // if the field is absent. Some Gemini chunks (e.g. the final finish chunk)
                // may not have a "text" field, so we skip them silently.
                if (!parts[0].TryGetProperty("text", out var textEl)) continue;
                var text = textEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;  // Send this token to the caller immediately

                // The last chunk from Gemini includes usageMetadata with the total token count.
                // We write it into UsageTracker so RateLimitMiddleware can update the DB
                // after this method returns. UsageTracker is Scoped — the middleware and
                // this service share the same instance within one HTTP request.
                if (doc.RootElement.TryGetProperty("usageMetadata", out var usage) &&
                    usage.TryGetProperty("totalTokenCount", out var totalTokens))
                {
                    _usageTracker.TotalTokens = totalTokens.GetInt32();
                }
            }
        }
    }

    // The default system prompt for Dynamo Python code generation.
    // Override via the Gemini:SystemPrompt config value without redeploying.
    // Using a raw string literal (""" ... """) avoids escaping special characters.
    private const string DefaultSystemPrompt = """
        Dynamo/Revit Python Script node assistant.

        Guidelines:
        - Output code only — no explanation text, no prose
        - If fixing existing code, state the bug in one sentence before the code
        - ALWAYS wrap every code snippet in ```python ... ``` fences — NEVER output raw code outside a code block
        - Only import what the script actually uses (clr, RevitAPI, etc.)
        - Default to IronPython 2 syntax unless the user specifies CPython 3
        - Do not add inline comments or explanatory comments inside generated code
        """;
}
