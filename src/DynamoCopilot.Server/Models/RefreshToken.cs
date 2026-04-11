namespace DynamoCopilot.Server.Models;

// =============================================================================
// RefreshToken — EF Core entity (maps to the "RefreshTokens" table)
// =============================================================================
//
// WHY DO WE NEED REFRESH TOKENS?
//
// JWT access tokens are short-lived (1 hour). When they expire, the user would
// have to log in again. Refresh tokens solve this: the client stores a long-lived
// refresh token (7 days) and uses it to silently get a new access token without
// asking the user for their password again.
//
// SECURITY MODEL:
// - Access token  → short-lived (1hr), stored in memory, used on every request
// - Refresh token → long-lived (7 days), stored securely by the client, used only to get new access tokens
//
// We NEVER store the raw refresh token in the database.
// We store a SHA-256 hash of it. This means:
//   - Even if someone reads your database, they get useless hashes
//   - The client has the only copy of the actual token
//
// WHY SHA-256 HERE INSTEAD OF BCRYPT (like passwords)?
// Refresh tokens are 512-bit cryptographically random values — they can't be guessed
// or brute-forced. BCrypt's slow hashing is designed for short, human-chosen passwords.
// SHA-256 is fast and sufficient for random tokens, and it lets us do efficient DB lookups.
// =============================================================================

public class RefreshToken
{
    public Guid Id { get; set; }

    // Foreign key to the Users table
    public Guid UserId { get; set; }

    // Navigation property — EF Core uses this to JOIN with the Users table.
    // When we query refresh tokens and call .Include(rt => rt.User), EF Core
    // populates this property with the related User object automatically.
    public User User { get; set; } = null!;

    // SHA-256 hash of the raw token. We index this column for fast lookups.
    public string TokenHash { get; set; } = string.Empty;

    // After this date, the token is invalid even if the hash matches
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
