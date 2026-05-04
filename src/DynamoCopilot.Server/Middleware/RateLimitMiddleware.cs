using System.Security.Claims;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using DynamoCopilot.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Middleware;

public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly int _defaultDailyRequests;

    public RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger, IConfiguration config)
    {
        _next = next;
        _logger = logger;
        _defaultDailyRequests = config.GetValue<int>("RateLimit:DailyRequestLimit", 200);
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db, UsageTracker usageTracker)
    {
        // Only rate-limit the chat endpoint
        if (!context.Request.Path.StartsWithSegments("/api/chat"))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

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

        if (!user.IsActive)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Your account has been deactivated. Please contact support."
            });
            return;
        }

        // ── DAILY RESET ───────────────────────────────────────────────────────
        // Before resetting, persist the previous day's counters to UsageLogs
        // so the dashboard can show historical daily/monthly breakdowns.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (user.LastResetDate != today)
        {
            if (user.LastResetDate.HasValue && (user.DailyRequestCount > 0 || user.DailyTokenCount > 0))
            {
                db.UsageLogs.Add(new UsageLog
                {
                    UserId       = user.Id,
                    Date         = user.LastResetDate.Value,
                    RequestCount = user.DailyRequestCount,
                    TokenCount   = user.DailyTokenCount
                });
            }

            user.DailyRequestCount = 0;
            user.DailyTokenCount   = 0;
            user.LastResetDate     = today;
            await db.SaveChangesAsync();
        }

        // ── REQUEST LIMIT ─────────────────────────────────────────────────────
        var requestLimit = user.RequestLimit ?? _defaultDailyRequests;
        if (user.DailyRequestCount >= requestLimit)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new
            {
                error  = "Daily request limit reached. Your quota resets at midnight UTC.",
                resetAt = DateTime.UtcNow.Date.AddDays(1)
            });
            return;
        }

        // Token usage is tracked but not capped — BYOK means the user's own
        // API key is charged, so enforcing a server-side token limit makes no sense.

        user.DailyRequestCount++;
        await db.SaveChangesAsync();

        await _next(context);

        // ── RECORD TOKEN USAGE (after stream completes) ───────────────────────
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
