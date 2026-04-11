using System.Security.Claims;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Middleware;

// =============================================================================
// RateLimitMiddleware — Enforces per-user daily request and token limits
// =============================================================================
//
// HOW MIDDLEWARE WORKS IN ASP.NET CORE:
//
//   Middleware is a class with an InvokeAsync(HttpContext, ...) method.
//   The RequestDelegate _next represents the REST of the pipeline after this middleware.
//   Calling `await _next(context)` passes the request down the pipeline.
//   Code BEFORE the call runs on the way IN; code AFTER runs on the way OUT.
//
//   public async Task InvokeAsync(HttpContext context)
//   {
//       // ← runs before the endpoint (request going IN)
//       await _next(context);
//       // ← runs after the endpoint returns (response going OUT)
//   }
//
// IMPORTANT: Singleton vs Scoped dependencies in middleware
//   Middleware is instantiated ONCE as a singleton. You CANNOT inject Scoped services
//   (like AppDbContext) through the constructor — the scoped service would be captured
//   forever and cause bugs (stale DbContext across requests).
//
//   The correct pattern: inject Scoped services as parameters of InvokeAsync().
//   ASP.NET Core calls InvokeAsync per-request and resolves those parameters fresh each time.
// =============================================================================

public class RateLimitMiddleware
{
    // Singleton dependencies go in the constructor (created once, thread-safe)
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;

    public RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    // Scoped dependencies go as InvokeAsync parameters (resolved fresh per request)
    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db,
        IConfiguration config,
        UsageTracker usageTracker)
    {
        // ── SKIP UNAUTHENTICATED REQUESTS ─────────────────────────────────────
        // Auth endpoints (/auth/register, /auth/login) and /health don't have a user.
        // If HttpContext.User isn't authenticated, just pass the request through.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        // ── IDENTIFY THE USER ─────────────────────────────────────────────────
        // The "sub" claim in the JWT is mapped to ClaimTypes.NameIdentifier
        // by the JWT Bearer middleware (UseAuthentication) before we get here.
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid token: user ID missing." });
            return;
        }

        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "User account not found." });
            return;
        }

        // ── CHECK LICENCE ─────────────────────────────────────────────────────
        if (!user.IsActive)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Your account has been deactivated. Please contact support."
            });
            return;
        }

        // ── LAZY DAILY RESET ──────────────────────────────────────────────────
        // Instead of a cron job that resets all users at midnight, we reset lazily:
        // on the first request of a new day, we zero out the counters here.
        // This approach needs no background job and is simple to reason about.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (user.LastResetDate != today)
        {
            user.DailyRequestCount = 0;
            user.DailyTokenCount = 0;
            user.LastResetDate = today;
            await db.SaveChangesAsync();
        }

        // ── RESOLVE EFFECTIVE LIMITS ──────────────────────────────────────────
        // Per-user limits (stored in DB) override the global defaults from config.
        // null in the DB means "use the global default".
        var requestLimit = user.RequestLimit
            ?? int.Parse(config["RateLimit:DailyRequestLimit"] ?? "30");

        var tokenLimit = user.TokenLimit
            ?? int.Parse(config["RateLimit:DailyTokenLimit"] ?? "40000");

        // ── CHECK LIMITS ──────────────────────────────────────────────────────
        if (user.DailyRequestCount >= requestLimit)
        {
            context.Response.StatusCode = 429; // 429 = Too Many Requests
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Daily request limit reached. Your usage resets at midnight UTC.",
                requestsUsed = user.DailyRequestCount,
                requestLimit
            });
            return;
        }

        if (user.DailyTokenCount >= tokenLimit)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Daily token limit reached. Your usage resets at midnight UTC.",
                tokensUsed = user.DailyTokenCount,
                tokenLimit
            });
            return;
        }

        // ── INCREMENT REQUEST COUNT ───────────────────────────────────────────
        // We increment BEFORE passing to the endpoint (not after) so that:
        //   a) A request counts even if the client disconnects mid-stream
        //   b) A user can't fire N requests simultaneously to bypass the limit
        user.DailyRequestCount++;
        await db.SaveChangesAsync();

        // ── PASS TO THE NEXT MIDDLEWARE / ENDPOINT ────────────────────────────
        await _next(context);

        // ── UPDATE TOKEN COUNT (runs after endpoint returns) ──────────────────
        // By the time we reach here, GeminiService has finished streaming and
        // has written the total token count into UsageTracker.
        // We add those tokens to the user's daily total.
        if (usageTracker.TotalTokens > 0)
        {
            user.DailyTokenCount += usageTracker.TotalTokens;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "User {UserId} used {Tokens} tokens. Daily total: {DailyTokens}/{TokenLimit}",
                userId, usageTracker.TotalTokens, user.DailyTokenCount, tokenLimit);
        }
    }
}
