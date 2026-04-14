using System.Text;
using System.Text.Json;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Embeddings;

// =============================================================================
// GeminiEmbedder — Generates embeddings via Gemini gemini-embedding-001
// =============================================================================

public class GeminiEmbedder
{
    public const int Dimensions = 768;
    private const int BatchSize = 100;

    // Free tier limit: 100 requests/min → 600ms per request minimum.
    // 700ms gives ~85 req/min with headroom for retries.
    private const int BatchDelayMs = 700;

    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    // All errors are appended here so they survive the \r progress display
    public readonly List<string> Errors = new();

    public GeminiEmbedder(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<float[]?[]> EmbedBatchAsync(
        IReadOnlyList<NodeRecord> records,
        CancellationToken ct = default)
    {
        var results = new float[]?[records.Count];

        for (int i = 0; i < records.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = records.Skip(i).Take(BatchSize).ToList();
            var embeddings = await EmbedSingleBatchWithRetryAsync(batch, ct);

            for (int j = 0; j < embeddings.Length; j++)
                results[i + j] = embeddings[j];

            if (i + BatchSize < records.Count)
                await Task.Delay(BatchDelayMs, ct);
        }

        return results;
    }

    private async Task<float[]?[]> EmbedSingleBatchWithRetryAsync(
        List<NodeRecord> batch,
        CancellationToken ct,
        int maxAttempts = 4)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await EmbedSingleBatchAsync(batch, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // Ctrl+C — propagate immediately, do not retry
            }
            catch (Exception ex)
            {
                lastEx = ex;

                bool isRateLimit = ex is HttpRequestException hre
                    && (hre.Message.StartsWith("429") || hre.Message.StartsWith("503"));

                if (isRateLimit && attempt < maxAttempts - 1)
                {
                    // The API tells us exactly how long to wait in the error body.
                    // Parse "retry in Xs" if present, otherwise fall back to 65s.
                    int waitMs = ParseRetryDelay(ex.Message) ?? 65_000;
                    Errors.Add($"Rate limited — waiting {waitMs / 1000}s before retry {attempt + 1}/{maxAttempts - 1}...");
                    await Task.Delay(waitMs, ct);
                }
                else if (!isRateLimit)
                {
                    // Non-retriable error — log and bail immediately
                    Errors.Add($"Batch failed (non-retriable): {ex.GetType().Name}: {ex.Message}");
                    return new float[]?[batch.Count];
                }
            }
        }

        // Exhausted retries (rate-limit path)
        Errors.Add($"Batch failed after {maxAttempts} attempts: {lastEx?.Message}");
        return new float[]?[batch.Count];
    }

    private async Task<float[]?[]> EmbedSingleBatchAsync(
        List<NodeRecord> batch,
        CancellationToken ct)
    {
        var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001" +
                  $":batchEmbedContents?key={_apiKey}";

        var requests = batch.Select(r => new
        {
            model    = "models/gemini-embedding-001",
            content  = new { parts = new[] { new { text = Truncate(r.EmbeddingText) } } },
            taskType = "RETRIEVAL_DOCUMENT",
            outputDimensionality = 768
        }).ToArray();

        var json = JsonSerializer.Serialize(
            new { requests },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Use ByteArrayContent so Content-Type is exactly "application/json" with no charset suffix
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)response.StatusCode}: {err}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (!doc.RootElement.TryGetProperty("embeddings", out var embeddingsEl))
        {
            Errors.Add($"Response missing 'embeddings' key. Body: {responseJson[..Math.Min(500, responseJson.Length)]}");
            return new float[]?[batch.Count];
        }

        var embeddings = embeddingsEl.EnumerateArray().ToArray();
        var results = new float[]?[batch.Count];

        for (int i = 0; i < Math.Min(embeddings.Length, batch.Count); i++)
        {
            if (!embeddings[i].TryGetProperty("values", out var valuesEl)) continue;
            results[i] = valuesEl.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }

        return results;
    }

    // Parses the suggested retry delay from the Gemini 429 error body.
    // The API embeds "retry in 59.47s" or retryDelay "59s" in the message.
    // Returns milliseconds, or null if not found.
    private static int? ParseRetryDelay(string errorMessage)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            errorMessage, @"retry in (\d+(?:\.\d+)?)s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var seconds))
            return (int)(seconds * 1000) + 2000; // add 2s buffer
        return null;
    }

    // gemini-embedding-001 supports up to 8192 tokens. 3000 chars is ~750 tokens — well under.
    private static string Truncate(string text) =>
        text.Length > 3000 ? text[..3000] : text;
}
