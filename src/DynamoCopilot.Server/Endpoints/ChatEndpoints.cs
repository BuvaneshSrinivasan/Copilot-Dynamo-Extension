using System.Text.Json;
using DynamoCopilot.Server.Models;
using DynamoCopilot.Server.Services;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// ChatEndpoints — Registers all /api/chat/* routes
// =============================================================================
//
// This file uses the "extension method on WebApplication" pattern.
//
// In Minimal APIs there are no controller classes (unlike the older MVC style).
// Instead, you map lambda functions or static methods directly to URL routes.
// Grouping them in static classes like this keeps Program.cs clean as the app grows.
//
// Pattern: define a static class with a method that takes `this WebApplication app`
// → call it in Program.cs as app.MapChatEndpoints()
// =============================================================================

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        // .RequireAuthorization() means: reject requests without a valid JWT with 401.
        // The JWT is read from the "Authorization: Bearer {token}" header.
        // UseAuthorization() middleware in Program.cs is what actually enforces this.
        app.MapPost("/api/chat/stream", HandleStreamAsync)
           .RequireAuthorization();
    }

    // ASP.NET Core's Minimal API engine inspects this method's parameters and
    // automatically resolves each one:
    //   ChatRequest request      → deserialized from the JSON request body
    //   ILlmService llmService   → injected from the DI container (GeminiService)
    //   HttpContext httpContext   → the raw HTTP context (needed for SSE headers + writing)
    //   CancellationToken ct     → automatically triggered when the client disconnects
    private static async Task HandleStreamAsync(
        ChatRequest request,
        ILlmService llmService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // ── VALIDATE INPUT ─────────────────────────────────────────────────────
        if (request.Messages is null || request.Messages.Count == 0)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsJsonAsync(
                new { error = "Messages list cannot be empty." },
                cancellationToken);
            return;
        }

        // ── SET SSE RESPONSE HEADERS ───────────────────────────────────────────
        // Server-Sent Events (SSE) is a simple HTTP-based protocol for one-way
        // streaming from server to client. The server keeps the connection open
        // and writes "data: ...\n\n" lines. The client reads them as they arrive.
        //
        // Three headers are required for SSE to work correctly:
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";

        // X-Accel-Buffering: no — tells nginx (which Railway uses as a reverse proxy)
        // NOT to buffer the response body. Without this, Railway collects all tokens
        // and sends them in one batch at the end — streaming would appear broken.
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        // ── STREAM TOKENS ──────────────────────────────────────────────────────
        try
        {
            await foreach (var token in llmService.StreamAsync(request.Messages, cancellationToken))
            {
                // SSE message format:  "data: <payload>\n\n"
                // The double newline (\n\n) is the message delimiter.
                // The client doesn't process a message until it receives the second \n.
                //
                // We wrap each token in a JSON object for forward-compatibility —
                // we can add fields (e.g. token index, model name) without breaking clients.
                var payload = JsonSerializer.Serialize(new { type = "token", value = token });
                await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);

                // FlushAsync forces the buffered bytes out to the network immediately.
                // Without this, the .NET runtime batches bytes for efficiency, which
                // makes streaming appear choppy or delayed (especially for short tokens).
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }

            // Signal to the client that the stream has ended.
            // The client should stop listening after receiving this event.
            var donePayload = JsonSerializer.Serialize(new { type = "done" });
            await httpContext.Response.WriteAsync($"data: {donePayload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected mid-stream — this is completely normal.
            // Examples: user closes Dynamo, navigates away, request times out.
            // We simply stop writing. No error logging needed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch ALL exceptions (bad model name, serialization errors, network issues, etc.)
            // and forward them as SSE error events instead of letting them become a 500.
            // Best-effort: the response may already be partially written, so we wrap in try/catch.
            try
            {
                var errPayload = JsonSerializer.Serialize(new { type = "error", message = ex.Message });
                await httpContext.Response.WriteAsync($"data: {errPayload}\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }
            catch { /* Response headers already sent — nothing more we can do */ }
        }
    }
}
