using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DynamoCopilot.Server.Models;
using Microsoft.IdentityModel.Tokens;

namespace DynamoCopilot.Server.Services;

// =============================================================================
// TokenService — Creates and validates JWT access tokens and refresh tokens
// =============================================================================

public class TokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenExpiryMinutes;
    private readonly int _refreshTokenExpiryDays;

    public TokenService(IConfiguration configuration)
    {
        _secret = configuration["Jwt:Secret"] is { Length: > 0 } s
            ? s
            : Environment.GetEnvironmentVariable("JWT__SECRET") is { Length: > 0 } envSecret
                ? envSecret
                : throw new InvalidOperationException(
                    "Jwt:Secret is not configured. " +
                    "Set it in appsettings.Development.json (local) or as JWT__SECRET env var in Railway.");

        _issuer = configuration["Jwt:Issuer"] ?? "DynamoCopilot";
        _audience = configuration["Jwt:Audience"] ?? "DynamoCopilot";
        _accessTokenExpiryMinutes = int.TryParse(configuration["Jwt:AccessTokenExpiryMinutes"], out var m) ? m : 60;
        _refreshTokenExpiryDays = int.TryParse(configuration["Jwt:RefreshTokenExpiryDays"], out var d) ? d : 7;
    }

    // ── ACCESS TOKEN ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a signed JWT access token for the given user.
    ///
    /// A JWT has three parts separated by dots: header.payload.signature
    ///   header    → algorithm used (HS256)
    ///   payload   → "claims" — key/value pairs about the user (not encrypted, just base64)
    ///   signature → HMAC-SHA256 of (header + payload) using your Jwt:Secret
    ///
    /// The server can verify the signature without storing the token — any tampered token
    /// will have an invalid signature and be rejected.
    /// </summary>
    // grantedExtensions: the list of extension names the user currently has an active
    // licence for (e.g. ["Copilot", "SuggestNodes"]). Each name becomes a separate
    // "ext" claim so the extension can check httpContext.User.FindAll("ext").
    // Multiple claims with the same name is the standard JWT way to encode an array.
    public string GenerateAccessToken(User user, IEnumerable<string> grantedExtensions)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var ext in grantedExtensions)
            claims.Add(new Claim("ext", ext));

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_accessTokenExpiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── REFRESH TOKEN ─────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a cryptographically random refresh token.
    /// This raw value is what the client stores and sends back.
    /// We NEVER store this in the database — only its SHA-256 hash.
    /// </summary>
    public string GenerateRawRefreshToken()
    {
        // RandomNumberGenerator is the cryptographically secure random generator.
        // (do NOT use System.Random for security purposes — it's predictable)
        var bytes = RandomNumberGenerator.GetBytes(64); // 512-bit random value
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Creates a RefreshToken entity ready to be saved to the database.
    /// The raw token is hashed — the hash is what gets stored.
    /// </summary>
    public RefreshToken CreateRefreshToken(Guid userId, string rawToken)
    {
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays)
        };
    }

    /// <summary>
    /// Hashes a raw token with SHA-256 for database storage/lookup.
    /// SHA-256 is deterministic — the same input always produces the same hash,
    /// so we can hash the incoming token and compare it to the stored hash.
    /// </summary>
    public string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }
}
