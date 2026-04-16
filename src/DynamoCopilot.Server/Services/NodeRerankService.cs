using System.Text;
using System.Text.Json;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// NodeRerankService — Uses Gemini to re-rank vector search candidates
// =============================================================================
// Phase B: NodeSearchService returns the top 40 via hybrid search (RRF-merged
// vector + keyword). This service passes those 40 + the user's query + their
// graph context to
// Gemini (non-streaming generateContent), which selects the 5–8 most useful
// nodes and writes a one-sentence reason for each.
//
// Why re-rank?
//   Cosine similarity is a good proxy but knows nothing about the user's
//   actual intent. Gemini reads the description, ports, and graph context to
//   pick nodes that actually fit the workflow — not just ones with similar
//   embeddings.
//
// Fallback:
//   If the Gemini call fails for any reason (quota, malformed JSON, timeout),
//   the service returns the top 8 candidates without reasons so the user
//   always gets a result.
// =============================================================================

public class NodeRerankService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly string _model;

    public NodeRerankService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini:ApiKey is not configured.");
        _model = configuration["Gemini:Model"] ?? "gemini-2.5-flash";
    }

    public async Task<IReadOnlyList<NodeSuggestionWithReason>> RerankAsync(
        string query,
        IReadOnlyList<NodeSuggestion> candidates,
        IReadOnlyList<string>? graphContext,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return Array.Empty<NodeSuggestionWithReason>();

        var prompt = BuildPrompt(query, candidates, graphContext);

        string geminiJson;
        try
        {
            geminiJson = await CallGeminiAsync(prompt, ct);
        }
        catch
        {
            // Gemini unavailable — degrade gracefully to top 8 without reasons
            return FallbackTop8(candidates);
        }

        return ParseResponse(geminiJson, candidates);
    }

    // ── Prompt builder ────────────────────────────────────────────────────────

    private static string BuildPrompt(
        string query,
        IReadOnlyList<NodeSuggestion> candidates,
        IReadOnlyList<string>? graphContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are helping an Autodesk Dynamo/Revit user find the best nodes for their workflow.");
        sb.AppendLine();
        sb.Append("User's goal: \"");
        sb.Append(query);
        sb.AppendLine("\"");

        if (graphContext?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Nodes already in their Dynamo graph (for context):");
            int limit = Math.Min(graphContext.Count, 20);
            for (int i = 0; i < limit; i++)
            {
                sb.Append("  - ");
                sb.AppendLine(graphContext[i]);
            }
        }

        sb.AppendLine();
        sb.Append("Candidate Dynamo nodes (0 to ");
        sb.Append(candidates.Count - 1);
        sb.AppendLine("):");

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.Append('[');
            sb.Append(i);
            sb.Append("] ");
            sb.Append(c.Name);
            sb.Append(" (");
            sb.Append(c.PackageName);
            sb.AppendLine(")");

            if (!string.IsNullOrWhiteSpace(c.Description))
            {
                sb.Append("    ");
                sb.AppendLine(c.Description!.Trim());
            }
            if (c.InputPorts?.Length > 0)
            {
                sb.Append("    Inputs: ");
                sb.AppendLine(string.Join(", ", c.InputPorts));
            }
            if (c.OutputPorts?.Length > 0)
            {
                sb.Append("    Outputs: ");
                sb.AppendLine(string.Join(", ", c.OutputPorts));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Select the 5 to 8 most useful nodes for the user's goal.");
        sb.AppendLine("Return ONLY a valid JSON array — no markdown, no commentary:");
        sb.AppendLine("[{\"index\": <number>, \"reason\": \"<one short sentence>\"}, ...]");

        return sb.ToString();
    }

    // ── Gemini HTTP call ──────────────────────────────────────────────────────

    private async Task<string> CallGeminiAsync(string prompt, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}" +
                  $":generateContent?key={_apiKey}";

        var bodyObj = new
        {
            contents = new[]
            {
                new
                {
                    role  = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            generation_config = new
            {
                thinking_config    = new { thinking_budget = 0 },
                response_mime_type = "application/json"
            }
        };

        var json = JsonSerializer.Serialize(bodyObj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Gemini Rerank API {(int)response.StatusCode}: {err}");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Response parser ───────────────────────────────────────────────────────

    private static IReadOnlyList<NodeSuggestionWithReason> ParseResponse(
        string geminiResponseJson,
        IReadOnlyList<NodeSuggestion> candidates)
    {
        try
        {
            // Navigate Gemini envelope: candidates[0].content.parts[0].text
            using var outer = JsonDocument.Parse(geminiResponseJson);
            var text = outer.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "[]";

            // Gemini sometimes wraps the JSON in a markdown code block even when
            // response_mime_type is set — strip it defensively.
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                var lastFence    = text.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline > 0 && lastFence > firstNewline)
                    text = text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }

            using var inner = JsonDocument.Parse(text);
            var result = new List<NodeSuggestionWithReason>();

            foreach (var item in inner.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var idxEl)) continue;
                var idx = idxEl.GetInt32();
                if (idx < 0 || idx >= candidates.Count) continue;

                var reason = item.TryGetProperty("reason", out var reasonEl)
                    ? reasonEl.GetString() ?? string.Empty
                    : string.Empty;

                result.Add(ToWithReason(candidates[idx], reason));
            }

            return result.Count > 0 ? result : FallbackTop8(candidates);
        }
        catch
        {
            return FallbackTop8(candidates);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<NodeSuggestionWithReason> FallbackTop8(IReadOnlyList<NodeSuggestion> candidates)
    {
        var result = new List<NodeSuggestionWithReason>();
        int limit  = Math.Min(candidates.Count, 8);
        for (int i = 0; i < limit; i++)
            result.Add(ToWithReason(candidates[i], string.Empty));
        return result;
    }

    private static NodeSuggestionWithReason ToWithReason(NodeSuggestion c, string reason)
        => new NodeSuggestionWithReason
        {
            Name        = c.Name,
            Category    = c.Category,
            PackageName = c.PackageName,
            Description = c.Description,
            InputPorts  = c.InputPorts  ?? Array.Empty<string>(),
            OutputPorts = c.OutputPorts ?? Array.Empty<string>(),
            Score       = c.Score,
            Reason      = reason
        };
}

// ── Response DTO (returned to extension clients) ──────────────────────────────

public sealed record NodeSuggestionWithReason
{
    public string   Name        { get; init; } = string.Empty;
    public string?  Category    { get; init; }
    public string   PackageName { get; init; } = string.Empty;
    public string?  Description { get; init; }
    public string[] InputPorts  { get; init; } = Array.Empty<string>();
    public string[] OutputPorts { get; init; } = Array.Empty<string>();
    public float    Score       { get; init; }

    /// <summary>
    /// One-sentence explanation of why Gemini selected this node for the query.
    /// Empty string when the fallback path was taken (Gemini call failed).
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
