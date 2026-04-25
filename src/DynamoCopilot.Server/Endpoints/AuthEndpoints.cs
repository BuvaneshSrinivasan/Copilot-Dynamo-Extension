using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using DynamoCopilot.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

// =============================================================================
// AuthEndpoints — /auth/* routes
// =============================================================================
//
// POST /auth/register  → create account (auto-activated, open registration)
// POST /auth/login     → verify credentials, return access + refresh tokens
// POST /auth/refresh   → exchange refresh token for a new access token
// =============================================================================

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // MapGroup creates a shared route prefix. All routes here are /auth/*
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
        // Basic input validation
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Results.BadRequest(new { error = "A valid email address is required." });

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });

        // Normalise email: trim whitespace + lowercase
        // Always normalise before storing or comparing so "Alice@Test.com" == "alice@test.com"
        var email = request.Email.Trim().ToLowerInvariant();

        // Check for duplicate email
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Results.Conflict(new { error = "An account with this email already exists." });

        // BCrypt.HashPassword handles salt generation automatically.
        // A salt is a random value mixed into the hash so two identical passwords
        // produce different hashes — prevents rainbow table attacks.
        var now = DateTime.UtcNow;
        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive = true, // open registration: everyone who signs up gets access
            LicenseStartDate = now,
            LicenseEndDate = now.AddMonths(6)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        // 201 Created — standard HTTP status for a successful resource creation
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

        // IMPORTANT: we return the same generic error whether the email doesn't exist
        // or the password is wrong. This prevents "email enumeration" — an attacker
        // probing which email addresses have accounts by observing different error messages.
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Results.Unauthorized();

        if (!user.IsActive)
            return Results.Json(
                new { error = "Your account has been deactivated. Please contact support." },
                statusCode: 403);

        if (user.LicenseEndDate.HasValue && user.LicenseEndDate.Value < DateTime.UtcNow)
            return Results.Json(
                new { error = "Your licence has expired. Please contact support to renew.", expiredAt = user.LicenseEndDate.Value },
                statusCode: 403);

        // Generate both tokens
        var accessToken = tokenService.GenerateAccessToken(user);
        var rawRefreshToken = tokenService.GenerateRawRefreshToken();
        var refreshTokenEntity = tokenService.CreateRefreshToken(user.Id, rawRefreshToken);

        db.RefreshTokens.Add(refreshTokenEntity);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: rawRefreshToken,   // raw token → client stores this
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

        // Hash the incoming token and look it up directly in the DB.
        // This is O(1) — we don't need to load all tokens and loop.
        // SHA-256 is deterministic: same input → same hash, every time.
        var tokenHash = tokenService.HashToken(request.RefreshToken);

        var refreshToken = await db.RefreshTokens
            .Include(rt => rt.User)       // also load the related User in the same query
            .FirstOrDefaultAsync(rt =>
                rt.TokenHash == tokenHash &&
                rt.ExpiresAt > DateTime.UtcNow, ct);

        if (refreshToken is null)
            return Results.Unauthorized();

        if (!refreshToken.User.IsActive)
            return Results.Json(new { error = "Account deactivated." }, statusCode: 403);

        // TOKEN ROTATION: delete the old refresh token and issue a new one.
        // This means each refresh token can only be used once.
        // If an attacker steals and uses a refresh token before the real client does,
        // the real client's next refresh will fail — alerting them that something is wrong.
        db.RefreshTokens.Remove(refreshToken);

        var newRawRefreshToken = tokenService.GenerateRawRefreshToken();
        var newRefreshTokenEntity = tokenService.CreateRefreshToken(refreshToken.UserId, newRawRefreshToken);
        db.RefreshTokens.Add(newRefreshTokenEntity);

        var accessToken = tokenService.GenerateAccessToken(refreshToken.User);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new AuthResponse(
            AccessToken: accessToken,
            RefreshToken: newRawRefreshToken,
            ExpiresAt: newRefreshTokenEntity.ExpiresAt));
    }
}
