using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Calls POST /api/nodes/suggest on the DynamoCopilot server and returns
    /// Gemini-re-ranked Dynamo node suggestions.
    ///
    /// Token handling mirrors <see cref="ServerLlmService"/>:
    ///   Proactive — calls <c>AuthService.GetValidTokenAsync()</c> before each request.
    ///   Reactive  — if the server returns 401, attempts one token refresh then retries.
    /// </summary>
    public sealed class NodeSuggestService : IDisposable
    {
        private readonly HttpClient  _httpClient;
        private readonly string      _serverUrl;
        private readonly AuthService _authService;

        public NodeSuggestService(string serverUrl, AuthService authService)
        {
            _serverUrl   = serverUrl.TrimEnd('/');
            _authService = authService;
            _httpClient  = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>
        /// Returns Gemini-re-ranked node suggestions for <paramref name="query"/>.
        /// </summary>
        /// <param name="query">Natural-language description of what the user needs.</param>
        /// <param name="graphContext">
        /// Optional array of node names already in the user's Dynamo graph.
        /// Sent as context so the re-ranker can avoid redundant suggestions.
        /// </param>
        public async Task<IReadOnlyList<NodeSuggestion>> SuggestAsync(
            string    query,
            string[]? graphContext,
            CancellationToken ct = default)
        {
            // ── 1. Proactive token check ──────────────────────────────────────
            var token = await _authService.GetValidTokenAsync();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Session expired. Please log in again.");

            var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["query"] = query, ["graphContext"] = graphContext });

            // ── 2. First attempt ──────────────────────────────────────────────
            var response = await PostAsync(token, body, ct);

            // ── 3. Reactive 401 handling ──────────────────────────────────────
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var refreshed = await _authService.RefreshAsync();
                if (!refreshed)
                    throw new InvalidOperationException("Session expired. Please log in again.");

                token    = await _authService.GetValidTokenAsync();
                response = await PostAsync(token!, body, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Session expired. Please log in again.");
                }
            }

            // ── 4. Parse & return ─────────────────────────────────────────────
            return await ParseResponseAsync(response, ct);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<HttpResponseMessage> PostAsync(
            string            token,
            string            body,
            CancellationToken ct)
        {
            // HttpRequestMessage cannot be reused after SendAsync — create a new one each time.
            using var request = new HttpRequestMessage(
                HttpMethod.Post, _serverUrl + "/api/nodes/suggest");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            request.Content =
                new StringContent(body, Encoding.UTF8, "application/json");

            return await _httpClient.SendAsync(request, ct);
        }

        private static async Task<IReadOnlyList<NodeSuggestion>> ParseResponseAsync(
            HttpResponseMessage response,
            CancellationToken   ct)
        {
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
                var err = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new HttpRequestException(
                    $"Node suggest error {(int)response.StatusCode}: {err}");
            }

            var json = await response.Content.ReadAsStringAsync();
            response.Dispose();

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("nodes", out var nodesEl))
                return Array.Empty<NodeSuggestion>();

            var result = new List<NodeSuggestion>();
            foreach (var n in nodesEl.EnumerateArray())
            {
                result.Add(new NodeSuggestion
                {
                    Name        = GetStr(n, "name"),
                    Category    = GetStrNull(n, "category"),
                    PackageName = GetStr(n, "packageName"),
                    Description = GetStrNull(n, "description"),
                    InputPorts  = GetStrArr(n, "inputPorts"),
                    OutputPorts = GetStrArr(n, "outputPorts"),
                    Score       = GetFloat(n, "score"),
                    Reason      = GetStr(n, "reason"),
                    NodeType    = GetStr(n, "nodeType")
                });
            }

            return result;
        }

        // ── JSON micro-helpers ────────────────────────────────────────────────

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        private static string? GetStrNull(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() : null;

        private static float GetFloat(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetSingle() : 0f;

        private static string[] GetStrArr(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return arr.EnumerateArray()
                .Select(x => x.GetString() ?? string.Empty)
                .ToArray();
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
