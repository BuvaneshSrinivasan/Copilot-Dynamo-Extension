using System.Text;
using System.Text.Json;
using Pgvector;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// EmbeddingService — Converts text into a 768-dimensional vector using
// Google Gemini's text-embedding-004 model.
// =============================================================================
// Used at query time by NodeSearchService to embed the user's search query
// before performing cosine similarity search against the DynamoNodes table.
// =============================================================================

public class EmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;

    // Dimension produced by text-embedding-004. Must match the vector(768)
    // column in the DynamoNodes table.
    public const int Dimensions = 768;

    public EmbeddingService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini:ApiKey is not configured.");
    }

    // Embeds a single text string. The query task type tells the model this
    // vector will be used to search for similar documents (not as a document
    // itself) — Gemini applies a query-optimised transformation.
    public async Task<Vector> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001" +
                  $":embedContent?key={_apiKey}";

        var body = new
        {
            model = "models/gemini-embedding-001",
            content = new { parts = new[] { new { text } } },
            taskType = "RETRIEVAL_QUERY",
            outputDimensionality = 768   // must match the vector(768) column
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
            throw new HttpRequestException($"Gemini Embed API {(int)response.StatusCode}: {err}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var values = doc.RootElement
            .GetProperty("embedding")
            .GetProperty("values")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();

        return new Vector(values);
    }
}
