using System.Security.Claims;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// UserEndpoints — /api/me, /api/usage/report
// =============================================================================
// /api/me            — returns the authenticated user's profile and usage stats
// /api/usage/report  — extension reports token/request counts after each direct
//                      AI call (Gemini, OpenAI, Claude, etc. called client-side)
// =============================================================================

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me", GetMeAsync).RequireAuthorization();
        app.MapPost("/api/usage/report", ReportUsageAsync).RequireAuthorization();
    }

    public sealed record UsageReportRequest(int Tokens, int Requests);

    private static async Task<IResult> GetMeAsync(
        HttpContext httpContext,
        AppDbContext db,
        CancellationToken ct)
    {
        // The JWT middleware has already validated the token and populated HttpContext.User.
        // ClaimTypes.NameIdentifier maps to the "sub" claim we set in TokenService.
        var userIdStr = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Results.Unauthorized();

        var user = await db.Users
            .Include(u => u.Licenses)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "User not found." });

        // Track which extension version the client is running.
        var clientVersion = httpContext.Request.Headers["X-Client-Version"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(clientVersion) && clientVersion != user.InstalledVersion)
            user.InstalledVersion = clientVersion;

        // Apply the daily reset here too — the middleware only covers /api/chat,
        // so without this the panel would display a stale count on days with no chat activity.
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
        }

        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;

        return Results.Ok(new
        {
            user.Email,
            user.DailyTokenCount,
            user.IsActive,
            Licenses = user.Licenses.Select(l => new
            {
                l.Extension,
                l.IsActive,
                l.EndDate,
                Expired = l.EndDate.HasValue && l.EndDate.Value < now
            })
        });
    }

    private static async Task<IResult> ReportUsageAsync(
        [FromBody] UsageReportRequest request,
        HttpContext httpContext,
        AppDbContext db,
        CancellationToken ct)
    {
        var userIdStr = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Results.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();
        if (!user.IsActive) return Results.Forbid();

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
        }

        user.DailyRequestCount += Math.Max(0, request.Requests);
        user.DailyTokenCount   += Math.Max(0, request.Tokens);
        await db.SaveChangesAsync(ct);

        return Results.Ok();
    }
}
