namespace DynamoCopilot.Server.Models;

// =============================================================================
// UserLicense — one row per extension a user has paid for
// =============================================================================
//
// Extension values are plain strings matching the extension names the JWT
// "ext" claim — e.g. "Copilot", "SuggestNodes".
//
// IsActive lets you revoke without deleting the row (keeps audit history).
// EndDate null means "never expires" (useful for lifetime licences).
// =============================================================================

public class UserLicense
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Extension identifier — matches the name checked by the Dynamo extension
    // e.g. "Copilot", "SuggestNodes"
    public string Extension { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
