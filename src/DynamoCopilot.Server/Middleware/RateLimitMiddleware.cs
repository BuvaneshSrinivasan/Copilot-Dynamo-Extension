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
    private readonly int _defaultDailyRequests;
    private readonly int _defaultDailyTokens;

    public RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger, IConfiguration config)
    {
        _next = next;
        _logger = logger;
        _defaultDailyRequests = config.GetValue<int>("RateLimit:DailyRequestLimit", 30);
        _defaultDailyTokens   = config.GetValue<int>("RateLimit:DailyTokenLimit",   40_000);
    }

    // Scoped dependencies go as InvokeAsync parameters (resolved fresh per request)
    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db,
        UsageTracker usageTracker)
    {
        // ── ONLY RATE-LIMIT THE CHAT ENDPOINT ─────────────────────────────────
        // Auth routes, /health, /api/me, etc. bypass this middleware entirely.
        if (!context.Request.Path.StartsWithSegments("/api/chat"))
        {
            await _next(context);
            return;
        }

        // ── SKIP UNAUTHENTICATED REQUESTS ─────────────────────────────────────
        // Should not happen for /api/chat/stream (RequireAuthorization rejects first),
        // but guard here as a safety net.
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

        // ── ENFORCE RATE LIMITS ───────────────────────────────────────────────
        var requestLimit = user.RequestLimit ?? _defaultDailyRequests;
        var tokenLimit   = user.TokenLimit   ?? _defaultDailyTokens;

        if (user.DailyRequestCount >= requestLimit)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Daily request limit reached. Your quota resets at midnight UTC.",
                resetAt = DateTime.UtcNow.Date.AddDays(1)
            });
            return;
        }

        if (user.DailyTokenCount >= tokenLimit)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Daily token limit reached. Your quota resets at midnight UTC.",
                resetAt = DateTime.UtcNow.Date.AddDays(1)
            });
            return;
        }

        // Increment request count before passing on (recorded even if the stream fails mid-way)
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
                "User {UserId} used {Tokens} tokens. Daily total: {DailyTokens}",
                userId, usageTracker.TotalTokens, user.DailyTokenCount);
        }
    }
}
