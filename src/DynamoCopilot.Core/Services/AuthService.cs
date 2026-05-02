using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    // =============================================================================
    // AuthService — owns the full authentication lifecycle
    // =============================================================================
    //
    // Responsibilities:
    //   • Login / Register via the server API
    //   • Persist tokens to %AppData%\DynamoCopilot\tokens.json
    //   • Proactively refresh the access token before it expires (< 5 min left)
    //   • Reactively refresh when the server returns 401
    //   • Fetch live usage stats from GET /api/me
    //   • Logout (clear persisted tokens + in-memory state)
    //
    // Token storage format (tokens.json):
    //   { accessToken, refreshToken, refreshExpiresAt, email }
    //
    // JWT expiry is decoded manually from the base64url payload — no extra NuGet
    // dependency needed since System.Text.Json is already referenced.
    // =============================================================================

    public sealed class AuthService : IDisposable
    {
        private readonly string _serverUrl;
        private readonly string _tokenFilePath;
        private readonly HttpClient _httpClient;

        private StoredTokens? _tokens;

        // ── Cross-extension auth state (static — same AppDomain, same DLL) ─────────
        // Fire-and-forget: VMs subscribe to keep their IsLoggedIn in sync across panels.

        public static event Action<string>? GlobalLoggedIn;
        public static event Action?         GlobalLoggedOut;

        // ── Public state ─────────────────────────────────────────────────────────

        public bool HasStoredTokens =>
            _tokens != null && !string.IsNullOrEmpty(_tokens.AccessToken);

        public string Email => _tokens?.Email ?? string.Empty;

        // ── Construction ─────────────────────────────────────────────────────────

        public AuthService(string serverUrl)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _tokenFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DynamoCopilot",
                "tokens.json");
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ── Startup: load + validate ─────────────────────────────────────────────

        /// <summary>
        /// Called once on startup. Loads tokens from disk and checks whether
        /// the refresh token is still valid (not expired).
        ///
        /// Returns true  → tokens loaded, user may be shown the chat screen
        ///                 (call GetValidTokenAsync() before the first API call
        ///                  to handle access-token refresh transparently).
        /// Returns false → no tokens or refresh token expired, show login screen.
        /// </summary>
        public bool TryLoadTokens()
        {
            if (!File.Exists(_tokenFilePath))
                return false;

            try
            {
                var fileBytes = File.ReadAllBytes(_tokenFilePath);
                bool isLegacy;
                string json;

                if (SecureStorage.TryDecrypt(fileBytes, out var decrypted))
                {
                    json     = decrypted!;
                    isLegacy = false;
                }
                else
                {
                    // Legacy plaintext file — read as UTF-8 and migrate on save
                    json     = Encoding.UTF8.GetString(fileBytes);
                    isLegacy = true;
                }

                var tokens = JsonSerializer.Deserialize<StoredTokens>(json);
                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
                    return false;

                // Refresh token expired → force re-login (don't even try to refresh)
                if (tokens.RefreshExpiresAt <= DateTime.UtcNow)
                    return false;

                _tokens = tokens;

                if (isLegacy)
                    PersistTokens(_tokens);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Token management ────────────────────────────────────────────────────

        /// <summary>
        /// Returns a valid access token, refreshing proactively if it expires
        /// within 5 minutes. Returns null if refresh fails (caller should show
        /// the login screen).
        /// </summary>
        public async Task<string?> GetValidTokenAsync()
        {
            if (_tokens == null || string.IsNullOrEmpty(_tokens.AccessToken))
                return null;

            // Proactive: if access token expires within 5 minutes, refresh now
            var expiry = DecodeJwtExpiry(_tokens.AccessToken);
            if (expiry > DateTime.UtcNow.AddMinutes(5))
                return _tokens.AccessToken;

            // Token is close to expiry or already expired — try refresh
            var refreshed = await RefreshAsync();
            return refreshed ? _tokens!.AccessToken : null;
        }

        /// <summary>
        /// Exchanges the stored refresh token for a new access+refresh pair.
        /// Called both proactively (near-expiry) and reactively (on 401).
        /// Returns false if the refresh token is expired or the server rejects it.
        /// </summary>
        public async Task<bool> RefreshAsync()
        {
            if (_tokens == null || string.IsNullOrEmpty(_tokens.RefreshToken))
                return false;

            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["refreshToken"] = _tokens.RefreshToken });
                using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/auth/refresh");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return false;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                _tokens.AccessToken   = root.GetProperty("accessToken").GetString()  ?? string.Empty;
                _tokens.RefreshToken  = root.GetProperty("refreshToken").GetString()  ?? string.Empty;
                _tokens.RefreshExpiresAt = root.GetProperty("expiresAt").GetDateTime();

                PersistTokens(_tokens);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Login / Register ─────────────────────────────────────────────────────

        public async Task<AuthResult> LoginAsync(string email, string password)
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
                using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/auth/login");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return AuthResult.Fail("Invalid email or password.");

                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return AuthResult.Fail("Your account has been deactivated. Please contact support.");

                if (!response.IsSuccessStatusCode)
                    return AuthResult.Fail($"Server error ({(int)response.StatusCode}). Please try again.");

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tokens = new StoredTokens
                {
                    AccessToken      = root.GetProperty("accessToken").GetString()  ?? string.Empty,
                    RefreshToken     = root.GetProperty("refreshToken").GetString() ?? string.Empty,
                    RefreshExpiresAt = root.GetProperty("expiresAt").GetDateTime(),
                    Email            = email.Trim().ToLowerInvariant()
                };

                _tokens = tokens;
                PersistTokens(tokens);
                GlobalLoggedIn?.Invoke(tokens.Email);
                return AuthResult.Ok(tokens);
            }
            catch (HttpRequestException)
            {
                return AuthResult.Fail("Cannot reach the server. Check your internet connection.");
            }
            catch (Exception ex)
            {
                return AuthResult.Fail($"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers a new account then immediately logs in to obtain tokens.
        /// Returns the same AuthResult shape as LoginAsync.
        /// </summary>
        public async Task<AuthResult> RegisterAsync(string email, string password)
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
                using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/auth/register");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.Conflict)
                    return AuthResult.Fail("An account with this email already exists.");

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Extract the server's validation message
                    var errJson = await response.Content.ReadAsStringAsync();
                    try
                    {
                        using var doc = JsonDocument.Parse(errJson);
                        if (doc.RootElement.TryGetProperty("error", out var errEl))
                            return AuthResult.Fail(errEl.GetString() ?? "Invalid input.");
                    }
                    catch { }
                    return AuthResult.Fail("Invalid input. Check your email and password.");
                }

                if (!response.IsSuccessStatusCode)
                    return AuthResult.Fail($"Registration failed ({(int)response.StatusCode}). Please try again.");

                // Registration succeeded — immediately log in to get tokens
                return await LoginAsync(email, password);
            }
            catch (HttpRequestException)
            {
                return AuthResult.Fail("Cannot reach the server. Check your internet connection.");
            }
            catch (Exception ex)
            {
                return AuthResult.Fail($"Unexpected error: {ex.Message}");
            }
        }

        // ── User info ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches live usage stats from GET /api/me.
        /// Returns null if the request fails (non-fatal — UI can show cached email).
        /// </summary>
        public async Task<UserInfo?> GetUserInfoAsync()
        {
            var token = await GetValidTokenAsync();
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _serverUrl + "/api/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<UserInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        // ── Logout ───────────────────────────────────────────────────────────────

        public void Logout()
        {
            _tokens = null;
            try { if (File.Exists(_tokenFilePath)) File.Delete(_tokenFilePath); }
            catch { }
            GlobalLoggedOut?.Invoke();
        }

        // ── Internal helpers ─────────────────────────────────────────────────────

        private void PersistTokens(StoredTokens tokens)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenFilePath)!);
                var json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllBytes(_tokenFilePath, SecureStorage.Encrypt(json));
            }
            catch { }
        }

        /// <summary>
        /// Decodes the "exp" (expiry) claim from a JWT without any NuGet dependency.
        ///
        /// A JWT is three base64url-encoded segments joined by dots:
        ///   header.payload.signature
        ///
        /// The payload is a JSON object containing standard claims like "exp"
        /// (Unix timestamp, seconds since 1970-01-01 UTC). We decode the middle
        /// segment and read "exp" directly.
        /// </summary>
        private static DateTime DecodeJwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return DateTime.MinValue;

                // Base64url → standard Base64 (pad + replace URL-safe chars)
                var payload = parts[1];
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                payload = payload.Replace('-', '+').Replace('_', '/');

                var bytes   = Convert.FromBase64String(payload);
                var jsonStr = Encoding.UTF8.GetString(bytes);

                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("exp", out var exp))
                    return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime;
            }
            catch { }

            return DateTime.MinValue;
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
