using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using DynamoCopilot.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// AuthEndpoints — /auth/* routes
// =============================================================================
//
// POST /auth/register  → create account (open registration, no licence granted)
// POST /auth/login     → verify credentials, return access + refresh tokens
// POST /auth/refresh   → exchange refresh token for a new access token
//
// The access token JWT contains one "ext" claim per extension the user holds
// an active licence for (e.g. ext=Copilot, ext=SuggestNodes).
// Licences are granted separately via POST /admin/grant.
// =============================================================================

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
    }

    // ── REGISTER ──────────────────────────────────────────────────────────────

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Results.BadRequest(new { error = "A valid email address is required." });

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });

        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Results.Conflict(new { error = "An account with this email already exists." });

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive = true
            // No licence is granted on registration — admin grants via POST /admin/grant
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/auth/users/{user.Id}", new { message = "Account created. You can now log in." });
    }

    // ── LOGIN ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        TokenService tokenService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return Results.BadRequest(new { error = "Email and password are required." });

        var email = request.Email.Trim().ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Return the same error whether the email doesn't exist or the password is wrong
        // to prevent email enumeration attacks.
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Results.Unauthorized();

        if (!user.IsActive)
            return Results.Json(
                new { error = "Your account has been deactivated. Please contact support." },
                statusCode: 403);

        // A user with no active licences can still log in — they just get an empty
        // "ext" claim. The extension shows a "no licence" banner for locked tools.
        var grantedExtensions = await ActiveLicenseNamesAsync(db, user.Id, ct);

        var accessToken = tokenService.GenerateAccessToken(user, grantedExtensions);
        var rawRefreshToken = tokenService.GenerateRawRefreshToken();
        var refreshTokenEntity = tokenService.CreateRefreshToken(user.Id, rawRefreshToken);

        db.RefreshTokens.Add(refreshTokenEntity);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: rawRefreshToken,
            ExpiresAt: refreshTokenEntity.ExpiresAt));
    }

    // ── REFRESH ───────────────────────────────────────────────────────────────

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        TokenService tokenService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Results.BadRequest(new { error = "Refresh token is required." });

        var tokenHash = tokenService.HashToken(request.RefreshToken);

        var refreshToken = await db.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt =>
                rt.TokenHash == tokenHash &&
                rt.ExpiresAt > DateTime.UtcNow, ct);

        if (refreshToken is null)
            return Results.Unauthorized();

        if (!refreshToken.User.IsActive)
            return Results.Json(new { error = "Account deactivated." }, statusCode: 403);

        // TOKEN ROTATION: delete old token, issue a new one so each refresh token is single-use.
        db.RefreshTokens.Remove(refreshToken);

        var newRawRefreshToken = tokenService.GenerateRawRefreshToken();
        var newRefreshTokenEntity = tokenService.CreateRefreshToken(refreshToken.UserId, newRawRefreshToken);
        db.RefreshTokens.Add(newRefreshTokenEntity);

        // Re-query licences so the refreshed token reflects any grants made since login.
        var grantedExtensions = await ActiveLicenseNamesAsync(db, refreshToken.UserId, ct);
        var accessToken = tokenService.GenerateAccessToken(refreshToken.User, grantedExtensions);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: newRawRefreshToken,
            ExpiresAt: newRefreshTokenEntity.ExpiresAt));
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    // Returns the extension names the user currently has an active, non-expired licence for.
    private static Task<List<string>> ActiveLicenseNamesAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.UserLicenses
            .Where(ul => ul.UserId == userId
                      && ul.IsActive
                      && (ul.EndDate == null || ul.EndDate > now))
            .Select(ul => ul.Extension)
            .ToListAsync(ct);
    }
}
