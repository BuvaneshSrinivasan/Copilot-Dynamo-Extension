using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Calls the DynamoCopilot server's /api/chat/stream endpoint.
    /// The server handles model selection based on the user's subscription tier.
    /// All LLM API keys are server-side — this client never touches them.
    /// </summary>
    public sealed class ServerLlmService : ILlmService, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly string _authToken;

        public ServerLlmService(string serverUrl, string authToken)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _authToken = authToken ?? string.Empty;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_serverUrl))
            {
                reason = "Server URL is not configured. Open the settings panel (\u2699) to set it.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_authToken))
            {
                reason = "Not logged in. Open the settings panel (\u2699) and save your auth token.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public async IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(out var reason))
                throw new InvalidOperationException(reason);

            // Build the request body: array of {role, content}
            var msgArray = new List<object>(messages.Count);
            foreach (var m in messages)
                msgArray.Add(new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });

            var body = JsonSerializer.Serialize(new { messages = msgArray });
            var endpoint = _serverUrl + "/api/chat/stream";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Auth token is invalid or expired. Please log in again via the settings panel.");

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Your current plan does not allow this request.");

            if (!response.IsSuccessStatusCode)
            {
                var body2 = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"Server error {(int)response.StatusCode}: {body2}");
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line.Substring("data: ".Length);
                // Server sends: {"type":"token","value":"..."} or {"type":"done"}
                string? token = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (type == "done") yield break;
                        if (type == "token" && root.TryGetProperty("value", out var valueEl))
                            token = valueEl.GetString();
                    }
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(token)) yield return token;
            }
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
