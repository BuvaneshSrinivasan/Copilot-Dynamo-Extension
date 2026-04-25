namespace DynamoCopilot.Server.Models;

// =============================================================================
// User — EF Core entity (maps directly to the "Users" table in PostgreSQL)
// =============================================================================
//
// An EF Core "entity" is a plain C# class that EF Core maps to a database table.
// Each property becomes a column. EF Core reads the class structure and generates
// the SQL CREATE TABLE statement for you via migrations.
//
// Rule of thumb for EF Core entities:
//   - Use a class (not record) — EF Core needs to track property changes
//   - Initialize string properties to string.Empty to avoid nullable warnings
//   - Use Guid for primary keys (better than int for distributed systems + security)
// =============================================================================

public class User
{
    // PRIMARY KEY
    // Guid = a 128-bit unique identifier (e.g. "3f2504e0-4f89-11d3-9a0c-0305e82c3301")
    // We'll tell PostgreSQL to generate this automatically via gen_random_uuid() in the migration.
    // Why Guid instead of int? Sequential int IDs expose your user count to the outside world
    // (userId=42 tells attackers you have ~42 users). Guids are opaque.
    public Guid Id { get; set; }

    // CREDENTIALS
    public string Email { get; set; } = string.Empty;

    // We never store the plain password — only the BCrypt hash.
    // BCrypt is a one-way hash: you can verify a password against it but can't reverse it.
    // Even if your database is stolen, passwords are safe.
    // Example hash: "$2a$11$rBnqhUMFuHqhj8c9jXJq8uVbZv3geFvGV9g..."
    public string PasswordHash { get; set; } = string.Empty;

    // LICENSE CONTROL
    // IsActive = the on/off switch for the user's licence.
    // Set to true on registration (open registration policy for early testers).
    // Flip to false via the admin API to revoke access immediately.
    // The rate limit middleware (Phase 4) will check this on every request.
    public bool IsActive { get; set; } = true;

    // RATE LIMITING (Phase 4 will enforce these)
    // These two counters reset daily. The reset happens lazily:
    // on the first request of a new day, we check if LastResetDate != today,
    // and if so, zero out both counters before checking the limits.
    public int DailyRequestCount { get; set; } = 0;
    public int DailyTokenCount { get; set; } = 0;

    // The date the counters were last reset to 0.
    // Nullable because it's null for brand-new users who haven't made any requests yet.
    // DateOnly (not DateTime) because we only care about the date, not the time.
    public DateOnly? LastResetDate { get; set; }

    // PER-USER LIMIT OVERRIDES
    // Nullable means "use the global default from config".
    // Set a specific value here to give a user a different limit from everyone else.
    // Example: a trusted beta tester might get RequestLimit = 100 while everyone else gets 30.
    public int? RequestLimit { get; set; }
    public int? TokenLimit { get; set; }

    // ADMIN NOTES
    // Free-text field for your own notes about a user.
    // Useful when you're managing 10-15 users and want to remember context:
    // e.g. "referred by John", "paying customer from March", "test account"
    public string? Notes { get; set; }

    // LICENSE VALIDITY
    // Set on registration and enforced by RateLimitMiddleware and the login endpoint.
    // Admins can extend LicenseEndDate via POST /admin/users/{id}/extend-license.
    public DateTime? LicenseStartDate { get; set; }
    public DateTime? LicenseEndDate { get; set; }

    // AUDIT
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
