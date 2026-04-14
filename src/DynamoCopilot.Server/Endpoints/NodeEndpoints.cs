using DynamoCopilot.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// NodeEndpoints — POST /api/nodes/suggest
// =============================================================================
// Accepts a natural-language query and returns ranked Dynamo node suggestions
// from the indexed package ecosystem stored in PostgreSQL + pgvector.
//
// Requires a valid JWT (same as /api/chat/stream).
// Phase B will add graph-context-aware re-ranking via Gemini.
// =============================================================================

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/nodes/suggest", HandleSuggestAsync)
           .RequireAuthorization();
    }

    private static async Task<IResult> HandleSuggestAsync(
        [FromBody] NodeSuggestRequest request,
        NodeSearchService searchService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required" });

        var nodes = await searchService.SuggestAsync(
            request.Query,
            request.Limit ?? 8,
            ct);

        return Results.Ok(new { nodes });
    }
}

// ── REQUEST DTO ───────────────────────────────────────────────────────────────

public sealed record NodeSuggestRequest(
    string Query,
    int? Limit     // optional, defaults to 8, max 20
);
