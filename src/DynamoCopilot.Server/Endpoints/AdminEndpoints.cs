using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// AdminEndpoints — /admin/* routes for managing users and licences
// =============================================================================
//
// SECURITY: Protected by X-Admin-Key header checked via an endpoint filter.
//
// Postman usage:
//   Add header:  X-Admin-Key: {your Admin:ApiKey value from config}
//
// Licence management workflow:
//   1. User signs up → registers an account (no licence yet)
//   2. User pays (you record it in your Excel sheet)
//   3. You call POST /admin/grant with their email, extension, and months
//   4. To revoke: POST /admin/revoke with their email and extension
// =============================================================================

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin");

        // ── ADMIN KEY FILTER ──────────────────────────────────────────────────
        group.AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expectedKey = config["Admin:ApiKey"];

            if (string.IsNullOrWhiteSpace(expectedKey))
                return Results.Json(new { error = "Admin access is not configured." }, statusCode: 503);

            var providedKey = context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();

            if (providedKey != expectedKey)
                return Results.Json(new { error = "Invalid or missing X-Admin-Key header." }, statusCode: 401);

            return await next(context);
        });

        // ── ROUTES ────────────────────────────────────────────────────────────
        group.MapGet("/users", GetUsersAsync);
        group.MapPost("/users", CreateUserAsync);
        group.MapPost("/grant", GrantLicenseAsync);
        group.MapPost("/revoke", RevokeLicenseAsync);
        group.MapPost("/users/{id:guid}/activate", ActivateUserAsync);
        group.MapPost("/users/{id:guid}/deactivate", DeactivateUserAsync);
        group.MapPost("/users/{id:guid}/reset-usage", ResetUsageAsync);
        group.MapPatch("/users/{id:guid}/limits", SetLimitsAsync);
        group.MapDelete("/users", DeleteUserAsync);
    }

    // ── GET /admin/users ──────────────────────────────────────────────────────
    // Returns all users sorted newest-first, including their current licences.

    private static async Task<IResult> GetUsersAsync(
        AppDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        var defaultRequestLimit = int.Parse(config["RateLimit:DailyRequestLimit"] ?? "30");
        var defaultTokenLimit = int.Parse(config["RateLimit:DailyTokenLimit"] ?? "40000");

        var now = DateTime.UtcNow;

        var users = await db.Users
            .Include(u => u.Licenses)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(users.Select(u => new
        {
            u.Id,
            u.Email,
            u.IsActive,
            u.DailyRequestCount,
            u.DailyTokenCount,
            u.RequestLimit,
            u.TokenLimit,
            EffectiveRequestLimit = u.RequestLimit ?? defaultRequestLimit,
            EffectiveTokenLimit = u.TokenLimit ?? defaultTokenLimit,
            u.LastResetDate,
            u.Notes,
            u.CreatedAt,
            Licenses = u.Licenses.Select(l => new
            {
                l.Extension,
                l.IsActive,
                l.StartDate,
                l.EndDate,
                Expired = l.EndDate.HasValue && l.EndDate.Value < now
            })
        }));
    }

    // ── POST /admin/grant ─────────────────────────────────────────────────────
    // Grants (or extends) a licence for a specific extension.
    // Uses email so you don't need to look up the user's GUID.
    // If a licence row already exists for that extension it is updated in place
    // (start date preserved, end date extended from today or the current end date).
    //
    // Request body:
    //   { "email": "user@example.com", "extension": "Copilot", "amount": 12, "unit": "months" }
    //   unit: "days" | "weeks" | "months" (default: "months")

    private static async Task<IResult> GrantLicenseAsync(
        GrantLicenseRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Extension))
            return Results.BadRequest(new { error = "email and extension are required." });

        if (request.Amount <= 0)
            return Results.BadRequest(new { error = "amount must be a positive integer." });

        var unit = (request.Unit ?? "months").ToLowerInvariant();
        if (unit is not ("days" or "weeks" or "months"))
            return Results.BadRequest(new { error = "unit must be 'days', 'weeks', or 'months'." });

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return Results.NotFound(new { error = $"No account found for '{email}'." });

        var now = DateTime.UtcNow;

        var existing = await db.UserLicenses
            .FirstOrDefaultAsync(ul => ul.UserId == user.Id && ul.Extension == request.Extension, ct);

        if (existing is null)
        {
            db.UserLicenses.Add(new UserLicense
            {
                UserId = user.Id,
                Extension = request.Extension,
                IsActive = true,
                StartDate = now,
                EndDate = AddDuration(now, request.Amount, unit)
            });
        }
        else
        {
            // Extend from today if expired, or stack on top of the current end date
            var baseDate = (existing.EndDate.HasValue && existing.EndDate.Value > now)
                ? existing.EndDate.Value
                : now;

            existing.IsActive = true;
            existing.EndDate = AddDuration(baseDate, request.Amount, unit);
        }

        await db.SaveChangesAsync(ct);

        var endDate = existing?.EndDate ?? AddDuration(now, request.Amount, unit);
        return Results.Ok(new
        {
            message = $"{request.Extension} licence granted to {email}.",
            extension = request.Extension,
            endDate
        });
    }

    // ── POST /admin/revoke ────────────────────────────────────────────────────
    // Revokes a user's licence for a specific extension immediately.
    // Sets IsActive = false — the row is kept for audit purposes.
    //
    // Request body:
    //   { "email": "user@example.com", "extension": "Copilot" }

    private static async Task<IResult> RevokeLicenseAsync(
        RevokeLicenseRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Extension))
            return Results.BadRequest(new { error = "email and extension are required." });

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return Results.NotFound(new { error = $"No account found for '{email}'." });

        var license = await db.UserLicenses
            .FirstOrDefaultAsync(ul => ul.UserId == user.Id && ul.Extension == request.Extension, ct);

        if (license is null)
            return Results.NotFound(new { error = $"{email} has no {request.Extension} licence to revoke." });

        license.IsActive = false;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { message = $"{request.Extension} licence revoked for {email}." });
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
    // Override global rate limits for a specific user.
    // Send null to revert to the global default.
    //
    // Request body:
    //   { "requestLimit": 100, "tokenLimit": 150000, "notes": "trusted beta tester" }

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

    // ── POST /admin/users ────────────────────────────────────────────────────
    // Creates an account directly without going through the self-registration flow.
    // SuggestNodes is auto-granted (same as /auth/register). Copilot requires a
    // separate POST /admin/grant call.
    //
    // Request body:
    //   { "email": "user@example.com", "password": "securepassword123" }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Results.BadRequest(new { error = "A valid email address is required." });

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });

        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Results.Conflict(new { error = $"An account already exists for '{email}'." });

        var user = new User
        {
            Id           = Guid.NewGuid(),
            Email        = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive     = true
        };

        db.Users.Add(user);
        db.UserLicenses.Add(new UserLicense
        {
            UserId    = user.Id,
            Extension = AppConstants.Extensions.SuggestNodes,
            IsActive  = true,
            StartDate = DateTime.UtcNow,
            EndDate   = null
        });

        await db.SaveChangesAsync(ct);

        return Results.Created($"/admin/users/{user.Id}", new
        {
            message = $"Account created for {email}.",
            userId  = user.Id
        });
    }

    // ── DELETE /admin/users/{id} ──────────────────────────────────────────────
    // Permanently removes the account. Cascade delete on FK handles UserLicenses,
    // RefreshTokens, and UsageLogs automatically.

    private static async Task<IResult> DeleteUserAsync(
        [FromBody] DeleteUserRequest request, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Results.BadRequest(new { error = "email is required." });

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return Results.NotFound(new { error = $"No account found for '{email}'." });

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { message = $"{user.Email} permanently deleted." });
    }

    private static DateTime AddDuration(DateTime date, int amount, string unit) => unit switch
    {
        "days"  => date.AddDays(amount),
        "weeks" => date.AddDays(amount * 7),
        _       => date.AddMonths(amount)
    };
}

public record CreateUserRequest(string Email, string Password);
public record DeleteUserRequest(string Email);
public record GrantLicenseRequest(string Email, string Extension, int Amount, string? Unit = "months");
public record RevokeLicenseRequest(string Email, string Extension);
public record SetLimitsRequest(int? RequestLimit, int? TokenLimit, string? Notes);
