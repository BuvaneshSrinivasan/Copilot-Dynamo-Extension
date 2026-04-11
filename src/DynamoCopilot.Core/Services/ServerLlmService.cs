using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Calls the DynamoCopilot server's /api/chat/stream endpoint.
    ///
    /// Token strategy (belt + suspenders):
    ///   Proactive — before each request, call AuthService.GetValidTokenAsync().
    ///               That method refreshes the access token if it expires in &lt;5 min.
    ///   Reactive  — if the server still returns 401 (e.g. clock skew, token
    ///               revoked), attempt one refresh and retry the request.
    ///               If the retry also fails, throw so the ViewModel can show
    ///               the login screen.
    /// </summary>
    public sealed class ServerLlmService : ILlmService, IDisposable
    {
        private readonly HttpClient  _httpClient;
        private readonly string      _serverUrl;
        private readonly AuthService _authService;

        public ServerLlmService(string serverUrl, AuthService authService)
        {
            _serverUrl   = serverUrl.TrimEnd('/');
            _authService = authService;
            _httpClient  = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public bool IsConfigured(out string reason)
        {
            if (_authService.HasStoredTokens) { reason = string.Empty; return true; }
            reason = "Not logged in.";
            return false;
        }

        public async IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // ── 1. Proactive token check ──────────────────────────────────────────
            var token = await _authService.GetValidTokenAsync();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Session expired. Please log in again.");

            // ── 2. First attempt ──────────────────────────────────────────────────
            var response = await SendRequestAsync(messages, token, cancellationToken);

            // ── 3. Reactive 401 handling ──────────────────────────────────────────
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var refreshed = await _authService.RefreshAsync();
                if (!refreshed)
                    throw new InvalidOperationException("Session expired. Please log in again.");

                token    = await _authService.GetValidTokenAsync();
                response = await SendRequestAsync(messages, token!, cancellationToken);

                // Second 401 → session is truly dead
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Session expired. Please log in again.");
                }
            }

            // ── 4. Other error status codes ───────────────────────────────────────
            if (response.StatusCode == (HttpStatusCode)429)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    "Daily limit reached. Your request or token limit for today has been exceeded.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    "Your account has been deactivated. Please contact support.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new HttpRequestException($"Server error {(int)response.StatusCode}: {errBody}");
            }

            // ── 5. Stream the SSE response ────────────────────────────────────────
            using (response)
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line) ||
                        !line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;

                    var data = line.Substring("data: ".Length);

                    string? chunk        = null;
                    string? serverError  = null;
                    try
                    {
                        using var doc  = JsonDocument.Parse(data);
                        var       root = doc.RootElement;

                        if (root.TryGetProperty("type", out var typeEl))
                        {
                            var type = typeEl.GetString();
                            if (type == "done") yield break;
                            if (type == "error" &&
                                root.TryGetProperty("message", out var errEl))
                                serverError = errEl.GetString() ?? "Unknown server error.";
                            else if (type == "token" &&
                                     root.TryGetProperty("value", out var valueEl))
                                chunk = valueEl.GetString();
                        }
                    }
                    catch (JsonException) { }

                    // Throw OUTSIDE the try/catch so iterators can propagate it cleanly
                    if (serverError != null)
                        throw new HttpRequestException($"Server error: {serverError}");

                    if (!string.IsNullOrEmpty(chunk))
                        yield return chunk;
                }
            }
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private async Task<HttpResponseMessage> SendRequestAsync(
            IReadOnlyList<ChatMessage> messages,
            string token,
            CancellationToken ct)
        {
            var msgArray = new List<object>(messages.Count);
            foreach (var m in messages)
                msgArray.Add(new
                {
                    role    = m.Role.ToString().ToLowerInvariant(),
                    content = m.Content
                });

            var body = JsonSerializer.Serialize(new { messages = msgArray });

            // HttpRequestMessage must be re-created for each attempt (cannot be reused
            // after SendAsync disposes the content stream).
            using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/api/chat/stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
