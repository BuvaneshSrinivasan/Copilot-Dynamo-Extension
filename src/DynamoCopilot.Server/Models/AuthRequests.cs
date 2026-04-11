namespace DynamoCopilot.Server.Models;

// Request and response records for the /auth/* endpoints.
// Using records keeps these concise — they're just data containers, nothing more.

/// <summary>POST /auth/register</summary>
public record RegisterRequest(string Email, string Password);

/// <summary>POST /auth/login</summary>
public record LoginRequest(string Email, string Password);

/// <summary>POST /auth/refresh</summary>
public record RefreshRequest(string RefreshToken);

/// <summary>
/// Returned by /auth/login and /auth/refresh.
/// AccessToken   → the JWT the client sends as "Authorization: Bearer {token}" on every request
/// RefreshToken  → long-lived token stored by the client, used only to get a new AccessToken
/// ExpiresAt     → when the AccessToken expires (UTC). Client should refresh before this.
/// </summary>
public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt);
