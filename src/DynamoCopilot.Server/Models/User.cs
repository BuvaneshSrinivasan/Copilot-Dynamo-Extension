namespace DynamoCopilot.Server.Models;

public enum UserTier
{
    Free = 0,
    Pro = 1,
    Enterprise = 2
}

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Primary email address from the OAuth provider.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>"google" or "github"</summary>
    public string OAuthProvider { get; set; } = string.Empty;

    /// <summary>The unique subject ID issued by the OAuth provider (sub claim).</summary>
    public string OAuthSubjectId { get; set; } = string.Empty;

    public UserTier Tier { get; set; } = UserTier.Free;

    public DateTime CreatedAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
