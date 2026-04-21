// DO NOT DELETE, SAVED FOR FUTURE USE — Ollama embedding service for server-side vector search.
using System.Text;
using System.Text.Json;
using Pgvector;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// EmbeddingService — Converts text into a 768-dimensional vector using
// Ollama nomic-embed-text, which matches the model used by the node indexer.
// =============================================================================
// IMPORTANT: The DynamoNodes table was populated by the node indexer using
// Ollama nomic-embed-text (768-dim). Query embeddings MUST use the same model
// or cosine similarity will be meaningless (different vector spaces).
// =============================================================================

public class EmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _ollamaUrl;

    // Dimension produced by nomic-embed-text. Must match the vector(768)
    // column in the DynamoNodes table.
    public const int Dimensions = 768;

    private const string Model = "nomic-embed-text";

    public EmbeddingService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _ollamaUrl = (configuration["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/');
    }

    // Embeds a single text string using Ollama's local nomic-embed-text model.
    // Returns a 768-dimensional vector ready for pgvector cosine similarity search.
    public async Task<Vector> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        var url = $"{_ollamaUrl}/api/embeddings";

        var body = new
        {
            model  = Model,
            prompt = text
        };

        var json = JsonSerializer.Serialize(body);

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama Embed API {(int)response.StatusCode}: {err}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var values = doc.RootElement
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();

        return new Vector(values);
    }
}
