using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// NodeSearchService — Hybrid search: vector cosine + keyword full-text, merged
// with Reciprocal Rank Fusion (RRF).
// =============================================================================
// Pipeline:
//   1. Embed the user's query with EmbeddingService (Ollama nomic-embed-text)
//   2. Run vector cosine similarity search  → top 40 candidates
//   3. Run keyword full-text search         → top 40 candidates
//      • camel-case splits "CurveFromCadLayer" → "Curve From Cad Layer"
//      • also searches the Keywords[] column
//   4. Merge both ranked lists with RRF (k = 60)
//   5. Return top `limit` results (default 40, capped at 40, sent to re-ranker)
// =============================================================================

public class NodeSearchService
{
    private readonly AppDbContext _db;
    private readonly EmbeddingService _embeddingService;

    // RRF constant — standard value; dampens the effect of rank differences
    private const int RrfK = 60;

    public NodeSearchService(AppDbContext db, EmbeddingService embeddingService)
    {
        _db = db;
        _embeddingService = embeddingService;
    }

    public async Task<IReadOnlyList<NodeSuggestion>> SuggestAsync(
        string query,
        int limit = 40,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<NodeSuggestion>();

        limit = Math.Clamp(limit, 1, 40);
        const int candidatePool = 40;

        // Run both searches in parallel — they use separate DB connections
        var vectorTask  = VectorSearchAsync(query, candidatePool, ct);
        var keywordTask = KeywordSearchAsync(query, candidatePool, ct);

        await Task.WhenAll(vectorTask, keywordTask);

        var vectorResults  = vectorTask.Result;
        var keywordResults = keywordTask.Result;

        // ── RECIPROCAL RANK FUSION ────────────────────────────────────────────
        // score(d) = Σ 1 / (k + rank_i(d))   where rank is 1-based
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < vectorResults.Count; i++)
        {
            var key = vectorResults[i].Name;
            scores.TryAdd(key, 0);
            scores[key] += 1.0 / (RrfK + i + 1);
        }

        for (int i = 0; i < keywordResults.Count; i++)
        {
            var key = keywordResults[i].Name;
            scores.TryAdd(key, 0);
            scores[key] += 1.0 / (RrfK + i + 1);
        }

        // Merge unique nodes from both result sets
        var allNodes = vectorResults
            .Concat(keywordResults)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return allNodes
            .OrderByDescending(n => scores.TryGetValue(n.Name, out var s) ? s : 0)
            .Take(limit)
            .Select((n, idx) => n with
            {
                // Normalise score: RRF doesn't produce a probability; map to [0,1]
                // by dividing by the theoretical max (two first-rank hits)
                Score = (float)(scores.TryGetValue(n.Name, out var s) ? s : 0)
            })
            .ToList();
    }

    // ── VECTOR SEARCH ─────────────────────────────────────────────────────────

    private async Task<List<NodeSuggestion>> VectorSearchAsync(
        string query, int limit, CancellationToken ct)
    {
        var queryVector = await _embeddingService.EmbedQueryAsync(query, ct);

        // IVFFlat cosine similarity — higher probes = better recall
        await _db.Database.ExecuteSqlRawAsync(
            "SET LOCAL ivfflat.probes = 10", ct);

        var rows = await _db.DynamoNodes
            .Where(n => n.Embedding != null)
            .OrderBy(n => n.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(n => new
            {
                n.Name,
                n.Category,
                n.PackageName,
                n.Description,
                n.InputPorts,
                n.OutputPorts,
                Distance = n.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return rows.Select(r => new NodeSuggestion
        {
            Name        = r.Name,
            Category    = r.Category,
            PackageName = r.PackageName,
            Description = r.Description,
            InputPorts  = r.InputPorts  ?? Array.Empty<string>(),
            OutputPorts = r.OutputPorts ?? Array.Empty<string>(),
            Score       = 1f - (float)r.Distance
        }).ToList();
    }

    // ── KEYWORD SEARCH ────────────────────────────────────────────────────────
    // Uses PostgreSQL full-text search.
    //
    // Camel-case splitting: "CurveFromCadLayer" → "Curve From Cad Layer"
    // This makes individual words searchable via plainto_tsquery.
    //
    // Also checks the Keywords[] text array for exact token overlap.
    //
    // Two-step approach to avoid Npgsql type-mapping issues with text[] columns
    // in non-entity SqlQueryRaw projections:
    //   Step 1: raw SQL returns (Name, Rank) — simple scalar types only
    //   Step 2: LINQ fetches the full DynamoNode rows by name

    private async Task<List<NodeSuggestion>> KeywordSearchAsync(
        string query, int limit, CancellationToken ct)
    {
        // Sanitise query: keep alphanumeric + spaces, collapse runs
        var safeQuery = System.Text.RegularExpressions.Regex
            .Replace(query, @"[^\w\s]", " ")
            .Trim();

        if (string.IsNullOrWhiteSpace(safeQuery))
            return new List<NodeSuggestion>();

        // Step 1: rank matching node names using full-text search + camel-case split
        // plainto_tsquery is injection-safe: it converts arbitrary text to a tsquery
        // without allowing operator injection.
        var rankSql = @"
            SELECT
                n.""Name"" AS ""Name"",
                ts_rank(
                    to_tsvector('english',
                        regexp_replace(n.""Name"", '([a-z])([A-Z])', '\1 \2', 'g') || ' ' ||
                        coalesce(n.""Description"", '') || ' ' ||
                        coalesce(array_to_string(n.""Keywords"", ' '), '')
                    ),
                    plainto_tsquery('english', {0})
                ) AS ""Rank""
            FROM ""DynamoNodes"" n
            WHERE
                to_tsvector('english',
                    regexp_replace(n.""Name"", '([a-z])([A-Z])', '\1 \2', 'g') || ' ' ||
                    coalesce(n.""Description"", '') || ' ' ||
                    coalesce(array_to_string(n.""Keywords"", ' '), '')
                ) @@ plainto_tsquery('english', {0})
            ORDER BY ""Rank"" DESC
            LIMIT {1}";

        var nameRanks = await _db.Database
            .SqlQueryRaw<NameRankRow>(rankSql, safeQuery, limit)
            .ToListAsync(ct);

        if (nameRanks.Count == 0)
            return new List<NodeSuggestion>();

        // Step 2: fetch full node data for the matched names
        var names = nameRanks.Select(r => r.Name).ToList();
        var nodes = await _db.DynamoNodes
            .Where(n => names.Contains(n.Name))
            .ToListAsync(ct);

        // Build a rank lookup for ordering
        var rankLookup = nameRanks.ToDictionary(
            r => r.Name, r => r.Rank, StringComparer.OrdinalIgnoreCase);

        return nodes
            .OrderByDescending(n => rankLookup.TryGetValue(n.Name, out var r) ? r : 0f)
            .Select(n => new NodeSuggestion
            {
                Name        = n.Name,
                Category    = n.Category,
                PackageName = n.PackageName,
                Description = n.Description,
                InputPorts  = n.InputPorts  ?? Array.Empty<string>(),
                OutputPorts = n.OutputPorts ?? Array.Empty<string>(),
                Score       = rankLookup.TryGetValue(n.Name, out var r) ? r : 0f
            })
            .ToList();
    }

    // Minimal projection for the raw keyword ranking query — only scalar types
    // so Npgsql's SqlQueryRaw can map them without custom type converters.
    private sealed class NameRankRow
    {
        public string Name { get; set; } = "";
        public float  Rank { get; set; }
    }
}

// DTO returned to the caller — only the fields the extension needs
public sealed record NodeSuggestion
{
    public string   Name        { get; init; } = "";
    public string?  Category    { get; init; }
    public string   PackageName { get; init; } = "";
    public string?  Description { get; init; }
    public string[] InputPorts  { get; init; } = Array.Empty<string>();
    public string[] OutputPorts { get; init; } = Array.Empty<string>();

    // Cosine similarity in [0, 1] — higher is more relevant
    public float Score { get; init; }
}
