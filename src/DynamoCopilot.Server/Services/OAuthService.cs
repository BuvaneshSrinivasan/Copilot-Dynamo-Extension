using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;

namespace DynamoCopilot.Server.Services;

/// <summary>
/// Handles the OAuth 2.0 authorization-code flow for Google and GitHub.
/// The extension opens a browser to the provider's consent screen, the user logs in,
/// the provider redirects to our /auth/{provider}/callback, we exchange the code for tokens,
/// fetch the user's profile, and return a JWT.
/// </summary>
public sealed class OAuthService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuthService(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    // --- Authorization URL builders ---

    public string BuildGoogleAuthUrl(string state, string redirectUri)
    {
        var clientId = RequireConfig("OAuth:Google:ClientId");
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = clientId;
        q["redirect_uri"] = redirectUri;
        q["response_type"] = "code";
        q["scope"] = "openid email profile";
        q["state"] = state;
        q["access_type"] = "online";
        return "https://accounts.google.com/o/oauth2/v2/auth?" + q;
    }

    public string BuildGithubAuthUrl(string state, string redirectUri)
    {
        var clientId = RequireConfig("OAuth:GitHub:ClientId");
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = clientId;
        q["redirect_uri"] = redirectUri;
        q["scope"] = "read:user user:email";
        q["state"] = state;
        return "https://github.com/login/oauth/authorize?" + q;
    }

    // --- Token exchange + profile fetch ---

    public async Task<OAuthUserInfo> ExchangeGoogleCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var clientId = RequireConfig("OAuth:Google:ClientId");
        var clientSecret = RequireConfig("OAuth:Google:ClientSecret");

        using var http = _httpClientFactory.CreateClient();

        // Exchange code for tokens
        var tokenResp = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }), ct);

        tokenResp.EnsureSuccessStatusCode();
        var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
        var tokenData = JsonDocument.Parse(tokenJson).RootElement;
        var accessToken = tokenData.GetProperty("access_token").GetString()!;

        // Fetch profile
        var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var profileResp = await http.SendAsync(profileReq, ct);
        profileResp.EnsureSuccessStatusCode();

        var profileJson = await profileResp.Content.ReadAsStringAsync(ct);
        var profile = JsonDocument.Parse(profileJson).RootElement;

        return new OAuthUserInfo(
            Provider: "google",
            SubjectId: profile.GetProperty("sub").GetString()!,
            Email: profile.GetProperty("email").GetString()!
        );
    }

    public async Task<OAuthUserInfo> ExchangeGitHubCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var clientId = RequireConfig("OAuth:GitHub:ClientId");
        var clientSecret = RequireConfig("OAuth:GitHub:ClientSecret");

        using var http = _httpClientFactory.CreateClient();

        // Exchange code for access token
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        tokenReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });
        // GitHub requires a User-Agent header
        tokenReq.Headers.UserAgent.ParseAdd("DynamoCopilot-Server/1.0");

        var tokenResp = await http.SendAsync(tokenReq, ct);
        tokenResp.EnsureSuccessStatusCode();
        var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
        var tokenData = JsonDocument.Parse(tokenJson).RootElement;
        var accessToken = tokenData.GetProperty("access_token").GetString()!;

        // Fetch profile
        var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        profileReq.Headers.UserAgent.ParseAdd("DynamoCopilot-Server/1.0");
        var profileResp = await http.SendAsync(profileReq, ct);
        profileResp.EnsureSuccessStatusCode();

        var profileJson = await profileResp.Content.ReadAsStringAsync(ct);
        var profile = JsonDocument.Parse(profileJson).RootElement;

        // GitHub may have a null email — fall back to the /user/emails endpoint
        var email = profile.TryGetProperty("email", out var emailProp) && emailProp.ValueKind != JsonValueKind.Null
            ? emailProp.GetString()!
            : await GetGitHubPrimaryEmailAsync(http, accessToken, ct);

        return new OAuthUserInfo(
            Provider: "github",
            SubjectId: profile.GetProperty("id").GetInt64().ToString(),
            Email: email
        );
    }

    private static async Task<string> GetGitHubPrimaryEmailAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.UserAgent.ParseAdd("DynamoCopilot-Server/1.0");
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var emails = JsonDocument.Parse(json).RootElement;
        foreach (var entry in emails.EnumerateArray())
        {
            if (entry.TryGetProperty("primary", out var primary) && primary.GetBoolean() &&
                entry.TryGetProperty("email", out var emailProp))
                return emailProp.GetString()!;
        }

        throw new InvalidOperationException("Could not retrieve a primary email from GitHub.");
    }

    private string RequireConfig(string key)
        => _config[key]
           ?? throw new InvalidOperationException($"Missing configuration: {key}");
}

public record OAuthUserInfo(string Provider, string SubjectId, string Email);
