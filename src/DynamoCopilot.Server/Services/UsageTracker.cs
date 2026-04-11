namespace DynamoCopilot.Server.Services;

// =============================================================================
// UsageTracker — Scoped "mailbox" that passes token counts between services
// =============================================================================
//
// PROBLEM: GeminiService knows the token count (from Gemini's usageMetadata),
// but RateLimitMiddleware needs it to update the database.
// They run in sequence — the middleware can't directly see what happens inside the endpoint.
//
// SOLUTION: Register UsageTracker as Scoped (one instance per HTTP request).
// Both GeminiService and RateLimitMiddleware are resolved from the SAME scope,
// so they get the SAME UsageTracker instance for the duration of one request.
//
// Flow:
//   1. Middleware starts, holds a reference to UsageTracker (empty at this point)
//   2. Middleware calls await _next(context) → endpoint runs
//   3. GeminiService streams tokens, reads usageMetadata from last Gemini chunk
//   4. GeminiService writes total token count into UsageTracker
//   5. Endpoint returns, control comes back to middleware
//   6. Middleware reads UsageTracker.TotalTokens and updates the database
//
// This is simpler and more readable than alternatives like
// IHttpContextAccessor, response body inspection, or a static/singleton.
// =============================================================================

public class UsageTracker
{
    /// <summary>
    /// Total tokens used in this request (input + output).
    /// Set by GeminiService once the final usageMetadata chunk arrives.
    /// Read by RateLimitMiddleware after the endpoint returns.
    /// </summary>
    public int TotalTokens { get; set; }
}
