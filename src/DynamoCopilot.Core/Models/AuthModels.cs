using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Tokens persisted to %AppData%\DynamoCopilot\tokens.json
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class StoredTokens
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>Refresh token expiry (7 days from issue). Used to know when
        /// the whole session is dead and the user must log in again.</summary>
        [JsonPropertyName("refreshExpiresAt")]
        public DateTime RefreshExpiresAt { get; set; }

        /// <summary>Email stored locally so the UI can greet the user without
        /// an extra network call on every startup.</summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Per-extension licence info inside UserInfo
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class UserLicenseInfo
    {
        [JsonPropertyName("extension")]
        public string Extension { get; set; } = string.Empty;

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("endDate")]
        public DateTime? EndDate { get; set; }

        [JsonPropertyName("expired")]
        public bool Expired { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Live usage data returned by GET /api/me
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class UserInfo
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("dailyTokenCount")]
        public int DailyTokenCount { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("licenses")]
        public List<UserLicenseInfo> Licenses { get; set; } = new List<UserLicenseInfo>();

        // Helper: find the licence row for a specific extension (null if not found).
        public UserLicenseInfo? GetLicense(string extensionId) =>
            Licenses.Find(l => string.Equals(l.Extension, extensionId, System.StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Result wrapper returned by AuthService login/register methods
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class AuthResult
    {
        public bool Success { get; private set; }
        public string? ErrorMessage { get; private set; }
        public StoredTokens? Tokens { get; private set; }

        private AuthResult() { }

        public static AuthResult Ok(StoredTokens tokens) =>
            new AuthResult { Success = true, Tokens = tokens };

        public static AuthResult Fail(string error) =>
            new AuthResult { Success = false, ErrorMessage = error };
    }
}
