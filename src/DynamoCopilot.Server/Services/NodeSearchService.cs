using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// NodeSearchService — Finds Dynamo nodes matching a natural-language query
// =============================================================================
// Phase A: pure cosine similarity search via pgvector.
//
// Query pipeline:
//   1. Embed the user's query using EmbeddingService (Gemini text-embedding-004)
//   2. Run an IVFFlat cosine similarity scan against DynamoNodes.Embedding
//   3. Return the top N results ordered by similarity (highest first)
//
// Phase B will add BM25 keyword boosting + Gemini re-ranking on top of this.
// =============================================================================

public class NodeSearchService
{
    private readonly AppDbContext _db;
    private readonly EmbeddingService _embeddingService;

    public NodeSearchService(AppDbContext db, EmbeddingService embeddingService)
    {
        _db = db;
        _embeddingService = embeddingService;
    }

    public async Task<IReadOnlyList<NodeSuggestion>> SuggestAsync(
        string query,
        int limit = 8,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        limit = Math.Clamp(limit, 1, 20);

        // ── 1. EMBED THE QUERY ────────────────────────────────────────────────
        var queryVector = await _embeddingService.EmbedQueryAsync(query, ct);

        // ── 2. COSINE SIMILARITY SEARCH ───────────────────────────────────────
        // CosineDistance returns values in [0, 2] where 0 = identical.
        // We convert to similarity = 1 - distance so higher = better.
        //
        // The IVFFlat index scans `probes` lists out of the 100 total.
        // Higher probes = better recall, slower query.
        // 10 probes gives ~95% recall for 100 lists — good for dev/prod.
        await _db.Database.ExecuteSqlRawAsync(
            "SET LOCAL ivfflat.probes = 10", ct);

        // Project to an anonymous type inside the DB query (EF-safe — no client-side
        // operators), then apply null defaults in-memory after materialisation.
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
}

// DTO returned to the caller — only the fields the extension needs
public sealed record NodeSuggestion
{
    public string Name { get; init; } = "";
    public string? Category { get; init; }
    public string PackageName { get; init; } = "";
    public string? Description { get; init; }
    public string[] InputPorts { get; init; } = [];
    public string[] OutputPorts { get; init; } = [];

    // Cosine similarity in [0, 1] — higher is more relevant
    public float Score { get; init; }
}
