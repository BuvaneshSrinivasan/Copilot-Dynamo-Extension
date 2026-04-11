using DynamoCopilot.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// AdminEndpoints — /admin/* routes for managing users and viewing usage
// =============================================================================
//
// SECURITY: Protected by X-Admin-Key header, checked via an endpoint filter.
//
// What is an endpoint filter?
// It's like middleware but scoped to a specific group of endpoints (not all requests).
// We attach it to the MapGroup("/admin") below, so it runs before EVERY handler in
// that group. If the key is wrong → 401, the handler never runs.
//
// Why not use JWT for admin? You call these from Postman — no login flow needed.
// A static secret key in a header is the simplest secure approach for personal use.
//
// Postman usage:
//   Add header:  X-Admin-Key: {your Admin:ApiKey value from config}
// =============================================================================

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin");

        // ── ADMIN KEY FILTER ──────────────────────────────────────────────────
        // AddEndpointFilter attaches a function that runs before every route in this group.
        // The filter receives the endpoint context and a `next` delegate.
        // Calling `await next(context)` passes execution to the actual handler.
        // Returning early (without calling next) short-circuits — the handler never runs.
        group.AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expectedKey = config["Admin:ApiKey"];

            // If Admin:ApiKey isn't configured, lock everything down
            if (string.IsNullOrWhiteSpace(expectedKey))
                return Results.Json(new { error = "Admin access is not configured." }, statusCode: 503);

            var providedKey = context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();

            if (providedKey != expectedKey)
                return Results.Json(new { error = "Invalid or missing X-Admin-Key header." }, statusCode: 401);

            return await next(context);
        });

        // ── ROUTES ────────────────────────────────────────────────────────────
        group.MapGet("/users", GetUsersAsync);
        group.MapPost("/users/{id:guid}/activate", ActivateUserAsync);
        group.MapPost("/users/{id:guid}/deactivate", DeactivateUserAsync);
        group.MapPost("/users/{id:guid}/reset-usage", ResetUsageAsync);
        group.MapPatch("/users/{id:guid}/limits", SetLimitsAsync);
    }

    // ── GET /admin/users ──────────────────────────────────────────────────────
    // Returns all users sorted by newest first, with their current usage stats.
    // The EffectiveXxxLimit fields show the limit actually in effect (per-user override
    // or global default), so you can see at a glance what each user's ceiling is.

    private static async Task<IResult> GetUsersAsync(
        AppDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        var defaultRequestLimit = int.Parse(config["RateLimit:DailyRequestLimit"] ?? "30");
        var defaultTokenLimit = int.Parse(config["RateLimit:DailyTokenLimit"] ?? "40000");

        var users = await db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.IsActive,
                u.DailyRequestCount,
                u.DailyTokenCount,
                u.RequestLimit,
                u.TokenLimit,
                // Show the effective limit so you can see at a glance what applies
                EffectiveRequestLimit = u.RequestLimit ?? defaultRequestLimit,
                EffectiveTokenLimit = u.TokenLimit ?? defaultTokenLimit,
                u.LastResetDate,
                u.Notes,
                u.CreatedAt
            })
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    // ── POST /admin/users/{id}/activate ───────────────────────────────────────

    private static async Task<IResult> ActivateUserAsync(
        Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return Results.NotFound(new { error = "User not found." });

        user.IsActive = true;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { message = $"{user.Email} activated." });
    }

    // ── POST /admin/users/{id}/deactivate ─────────────────────────────────────

    private static async Task<IResult> DeactivateUserAsync(
        Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return Results.NotFound(new { error = "User not found." });

        user.IsActive = false;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { message = $"{user.Email} deactivated." });
    }

    // ── POST /admin/users/{id}/reset-usage ────────────────────────────────────
    // Manually resets a user's daily counters — useful if you want to give
    // someone extra requests before the midnight automatic reset.

    private static async Task<IResult> ResetUsageAsync(
        Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return Results.NotFound(new { error = "User not found." });

        user.DailyRequestCount = 0;
        user.DailyTokenCount = 0;
        user.LastResetDate = DateOnly.FromDateTime(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { message = $"Usage reset for {user.Email}." });
    }

    // ── PATCH /admin/users/{id}/limits ────────────────────────────────────────
    // Override the global rate limits for a specific user.
    // Send null for RequestLimit or TokenLimit to revert to the global default.
    //
    // Request body example:
    //   { "requestLimit": 100, "tokenLimit": 150000, "notes": "trusted beta tester" }
    //   { "requestLimit": null, "tokenLimit": null }   ← revert to global defaults

    private static async Task<IResult> SetLimitsAsync(
        Guid id,
        SetLimitsRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return Results.NotFound(new { error = "User not found." });

        user.RequestLimit = request.RequestLimit;
        user.TokenLimit = request.TokenLimit;

        // Only update Notes if a value was provided (don't wipe existing notes)
        if (request.Notes is not null)
            user.Notes = request.Notes;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            message = $"Limits updated for {user.Email}.",
            user.RequestLimit,
            user.TokenLimit,
            user.Notes
        });
    }
}

/// <summary>Request body for PATCH /admin/users/{id}/limits</summary>
public record SetLimitsRequest(int? RequestLimit, int? TokenLimit, string? Notes);
