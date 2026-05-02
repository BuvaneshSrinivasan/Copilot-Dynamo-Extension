// DO NOT DELETE, SAVED FOR FUTURE USE — server-side node suggestion endpoint for the subscription model.
using DynamoCopilot.Server.Filters;
using DynamoCopilot.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// NodeEndpoints — POST /api/nodes/suggest
// =============================================================================
// Phase A: pure vector search (top N by cosine similarity).
// Phase B: vector search fetches top 20, Gemini re-ranks to 5–8 with reasons.
//          Accepts an optional GraphContext array so the re-ranker knows what
//          nodes are already in the user's graph.
// =============================================================================

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/nodes/suggest", HandleSuggestAsync)
           .RequireAuthorization()
           .AddEndpointFilter(LicenseFilter.Require(AppConstants.Extensions.SuggestNodes));
    }

    private static async Task<IResult> HandleSuggestAsync(
        [FromBody] NodeSuggestRequest request,
        NodeSearchService             searchService,
        NodeRerankService             rerankService,
        CancellationToken             ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required" });

        // ── 1. Hybrid search: top 40 candidates (vector + keyword, RRF merged) ─
        var candidates = await searchService.SuggestAsync(request.Query, 40, ct);

        // ── 2. Gemini re-rank: pick best 5–8 with reasons ────────────────────
        var ranked = await rerankService.RerankAsync(
            request.Query,
            candidates,
            request.GraphContext,
            ct);

        return Results.Ok(new { nodes = ranked });
    }
}

// ── REQUEST DTO ───────────────────────────────────────────────────────────────

public sealed record NodeSuggestRequest(
    string    Query,
    int?      Limit,          // kept for API compatibility; now ignored (always 20 candidates)
    string[]? GraphContext     // names of nodes currently in the user's Dynamo graph
);
