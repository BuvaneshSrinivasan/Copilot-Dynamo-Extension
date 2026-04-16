using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DynamoCopilot.Server.Models;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// OpenAiService — Streams responses from the OpenAI Chat Completions API
// =============================================================================
//
// OpenAI streaming endpoint:
//   POST https://api.openai.com/v1/chat/completions
//   Authorization: Bearer {apiKey}
//   Body: { "model": "...", "stream": true, "stream_options": {"include_usage": true}, ... }
//
// The response is a stream of SSE lines. Each data line carries a chunk:
//
//   data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":null,...}],...}
//   data: {"choices":[{"delta":{"content":" there!"},"finish_reason":null,...}],...}
//   data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"total_tokens":12},...}
//   data: [DONE]
//
// "stream_options": {"include_usage": true} tells OpenAI to include token usage
// on the LAST chunk (alongside the finish_reason:"stop" chunk, or just before [DONE]).
// We write totalTokens into UsageTracker so RateLimitMiddleware can update the
// user's daily token count after the stream ends.
// =============================================================================

public class OpenAiService : ILlmService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UsageTracker _usageTracker;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;

    public OpenAiService(IHttpClientFactory httpClientFactory, IConfiguration configuration, UsageTracker usageTracker)
    {
        _httpClientFactory = httpClientFactory;
        _usageTracker = usageTracker;

        _apiKey = configuration["OpenAi:ApiKey"] is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException(
                "OpenAi:ApiKey is not configured. " +
                "Run: dotnet user-secrets set \"OpenAi:ApiKey\" \"YOUR_KEY\" " +
                "or set the OPENAI__APIKEY environment variable in Railway.");

        _model = configuration["OpenAi:Model"] ?? "gpt-4.5-mini";

        _systemPrompt = configuration["OpenAi:SystemPrompt"] is { Length: > 0 } sp
            ? sp
            : DefaultSystemPrompt;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ── 1. BUILD THE REQUEST BODY ──────────────────────────────────────────
        // OpenAI's format uses "system", "user", and "assistant" roles.
        // We prepend our system prompt as the first message in the array.
        // OpenAI doesn't have a separate system_instruction field like Gemini —
        // the system role is just another message at the top of the conversation.
        var openAiMessages = messages
            .Select(m => new { role = m.Role, content = m.Content })
            .ToList();

        // Prepend the system prompt as a system-role message
        var allMessages = openAiMessages.Prepend(new { role = "system", content = _systemPrompt });

        var requestBody = new
        {
            model = _model,
            messages = allMessages,
            stream = true,
            // include_usage: tells OpenAI to send a final chunk with token counts.
            // Without this the usage field is always null when streaming.
            stream_options = new { include_usage = true }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        // ── 2. SEND THE REQUEST ────────────────────────────────────────────────
        var httpClient = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // OpenAI uses Bearer token auth in the Authorization header,
        // not a query-string key like Gemini.
        httpRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        // ResponseHeadersRead is CRITICAL for streaming — without it HttpClient
        // buffers the entire response before returning, defeating the purpose.
        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"OpenAI API returned {(int)response.StatusCode}: {errorBody}");
        }

        // ── 3. READ AND PARSE THE SSE STREAM ──────────────────────────────────
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var data = line["data: ".Length..];

            // OpenAI signals the end of the stream with the literal string "[DONE]"
            if (data == "[DONE]")
                break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (doc)
            {
                // Navigate: root → choices[0] → delta → content
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentEl))
                    {
                        var text = contentEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                            yield return text;
                    }
                }

                // The final chunk (with include_usage: true) contains a top-level
                // "usage" field with the total token count for this request.
                if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                    usage.TryGetProperty("total_tokens", out var totalTokens))
                {
                    _usageTracker.TotalTokens = totalTokens.GetInt32();
                }
            }
        }
    }

    private const string DefaultSystemPrompt = """
        You are a coding assistant embedded inside Autodesk Dynamo, a visual programming
        tool used with Autodesk Revit. Your job is to help users write and fix Python scripts
        for Dynamo's Python Script nodes.

        Guidelines:
        - Write clean, correct Python that runs inside Dynamo's Python Script node
        - Include the standard Dynamo boilerplate imports when relevant (clr, RevitAPI, etc.)
        - Keep responses concise: one short explanation sentence, then the code
        - If fixing existing code, state the bug in one sentence before showing the fix
        - Format all code in ```python blocks
        - Default to IronPython 2 syntax unless the user specifies CPython 3
        """;
}
