using System.Text;
using System.Text.Json;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Embeddings;

// =============================================================================
// OllamaEmbedder — Generates embeddings via a local Ollama instance
// =============================================================================
// Uses the POST /api/embed endpoint (Ollama 0.1.31+) which accepts a list of
// inputs in one call — effectively free batching with no rate limits.
//
// Default model: nomic-embed-text
//   - 768-dimensional output  → matches the existing vector(768) DB column
//   - ~274MB on disk, runs on CPU or GPU
//   - Pull once with: ollama pull nomic-embed-text
//
// No API key needed. No rate limits. Runs entirely on your machine.
// =============================================================================

public class OllamaEmbedder
{
    public const int Dimensions = 768;
    private const int BatchSize = 50; // items per /api/embed call

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public readonly List<string> Errors = new();

    public OllamaEmbedder(string baseUrl = "http://localhost:11434", string model = "nomic-embed-text")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model   = model;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // Verifies Ollama is reachable and the model is loaded before the main run.
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models").EnumerateArray();
            bool found = models.Any(m =>
                m.GetProperty("name").GetString()?.StartsWith(_model, StringComparison.OrdinalIgnoreCase) == true);

            if (!found)
                Errors.Add($"Model '{_model}' not found in Ollama. Run: ollama pull {_model}");

            return found;
        }
        catch (Exception ex)
        {
            Errors.Add($"Ollama not reachable at {_baseUrl}: {ex.Message}");
            return false;
        }
    }

    // Embeds all records in batches. Returns a parallel array of float[] results
    // (null for any record that failed to embed).
    public async Task<float[]?[]> EmbedBatchAsync(
        IReadOnlyList<NodeRecord> records,
        CancellationToken ct = default)
    {
        var results = new float[]?[records.Count];

        for (int i = 0; i < records.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = records.Skip(i).Take(BatchSize).ToList();
            var embeddings = await EmbedSingleBatchAsync(batch, ct);

            for (int j = 0; j < embeddings.Length; j++)
                results[i + j] = embeddings[j];
        }

        return results;
    }

    private async Task<float[]?[]> EmbedSingleBatchAsync(
        List<NodeRecord> batch,
        CancellationToken ct)
    {
        var inputs = batch.Select(r => Truncate(r.EmbeddingText)).ToArray();

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            input = inputs
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync($"{_baseUrl}/api/embed", content, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Errors.Add($"HTTP error: {ex.Message}");
            return new float[]?[batch.Count];
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            Errors.Add($"Ollama {(int)response.StatusCode}: {err[..Math.Min(300, err.Length)]}");
            return new float[]?[batch.Count];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("embeddings", out var embeddingsEl))
        {
            Errors.Add($"Response missing 'embeddings'. Body: {json[..Math.Min(300, json.Length)]}");
            return new float[]?[batch.Count];
        }

        var rows = embeddingsEl.EnumerateArray().ToArray();
        var results = new float[]?[batch.Count];

        for (int i = 0; i < Math.Min(rows.Length, batch.Count); i++)
            results[i] = rows[i].EnumerateArray().Select(v => v.GetSingle()).ToArray();

        return results;
    }

    // nomic-embed-text context window is 8192 tokens. 3000 chars ≈ 750 tokens.
    private static string Truncate(string text) =>
        text.Length > 3000 ? text[..3000] : text;
}
