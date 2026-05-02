using System.Security.Claims;
using DynamoCopilot.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// UserEndpoints — /api/me
// =============================================================================
// Returns the authenticated user's profile and current usage stats.
// Used by the Dynamo extension to populate the user info panel.
// =============================================================================

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me", GetMeAsync).RequireAuthorization();
    }

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
}
