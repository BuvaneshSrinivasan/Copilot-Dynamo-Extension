using DynamoCopilot.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace DynamoCopilot.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Login initiators ---
        // Extension calls these to get the URL to open in the browser.
        // The port parameter tells the server where to redirect after login
        // (the extension listens on a random localhost port for the callback).

        app.MapGet("/auth/google/login", (
            [FromQuery] int port,
            OAuthStateService stateService,
            OAuthService oauthService,
            IConfiguration config) =>
        {
            if (port is < 1024 or > 65535)
                return Results.BadRequest(new { error = "Invalid port." });

            var state = stateService.CreateState(port);
            var redirectUri = BuildServerCallbackUri(config, "google");
            var url = oauthService.BuildGoogleAuthUrl(state, redirectUri);

            return Results.Ok(new { url });
        })
        .WithTags("Auth");

        app.MapGet("/auth/github/login", (
            [FromQuery] int port,
            OAuthStateService stateService,
            OAuthService oauthService,
            IConfiguration config) =>
        {
            if (port is < 1024 or > 65535)
                return Results.BadRequest(new { error = "Invalid port." });

            var state = stateService.CreateState(port);
            var redirectUri = BuildServerCallbackUri(config, "github");
            var url = oauthService.BuildGithubAuthUrl(state, redirectUri);

            return Results.Ok(new { url });
        })
        .WithTags("Auth");

        // --- OAuth Callbacks ---
        // Google and GitHub redirect here after the user grants consent.
        // We exchange the code, upsert the user, issue a JWT, then redirect to
        // the extension's local listener so its browser window can close automatically.

        app.MapGet("/auth/google/callback", async (
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error,
            OAuthStateService stateService,
            OAuthService oauthService,
            UserService userService,
            JwtService jwtService,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
                return CallbackError($"Google denied access: {error}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return CallbackError("Missing code or state.");

            var port = stateService.ConsumeState(state);
            if (port is null)
                return CallbackError("Invalid or expired state.");

            try
            {
                var redirectUri = BuildServerCallbackUri(config, "google");
                var userInfo = await oauthService.ExchangeGoogleCodeAsync(code, redirectUri, ct);
                var user = await userService.UpsertAsync(userInfo.Provider, userInfo.SubjectId, userInfo.Email, ct);
                var jwt = jwtService.CreateToken(user);

                return RedirectToExtension(port.Value, jwt);
            }
            catch (Exception ex)
            {
                return CallbackError($"Auth failed: {ex.Message}");
            }
        })
        .WithTags("Auth");

        app.MapGet("/auth/github/callback", async (
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error,
            OAuthStateService stateService,
            OAuthService oauthService,
            UserService userService,
            JwtService jwtService,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
                return CallbackError($"GitHub denied access: {error}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return CallbackError("Missing code or state.");

            var port = stateService.ConsumeState(state);
            if (port is null)
                return CallbackError("Invalid or expired state.");

            try
            {
                var redirectUri = BuildServerCallbackUri(config, "github");
                var userInfo = await oauthService.ExchangeGitHubCodeAsync(code, redirectUri, ct);
                var user = await userService.UpsertAsync(userInfo.Provider, userInfo.SubjectId, userInfo.Email, ct);
                var jwt = jwtService.CreateToken(user);

                return RedirectToExtension(port.Value, jwt);
            }
            catch (Exception ex)
            {
                return CallbackError($"Auth failed: {ex.Message}");
            }
        })
        .WithTags("Auth");

        return app;
    }

    // Sends the JWT back to the extension's local HTTP listener via a browser redirect.
    // The extension's listener parses the ?token= query param and closes the browser window.
    private static IResult RedirectToExtension(int port, string jwt)
        => Results.Redirect($"http://localhost:{port}/auth/complete?token={Uri.EscapeDataString(jwt)}");

    private static IResult CallbackError(string message)
        => Results.Text(
            $"<html><body><h2>Login failed</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p>" +
            "<p>You can close this window.</p></body></html>",
            "text/html",
            statusCode: 400);

    // Builds the absolute URL for our callback endpoint (must match what's registered in Google/GitHub OAuth app).
    private static string BuildServerCallbackUri(IConfiguration config, string provider)
    {
        var baseUrl = config["App:BaseUrl"]
            ?? throw new InvalidOperationException("App:BaseUrl is not configured.");
        return $"{baseUrl.TrimEnd('/')}/auth/{provider}/callback";
    }
}
