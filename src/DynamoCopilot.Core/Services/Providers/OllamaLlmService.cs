using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services.Providers
{
    /// <summary>
    /// Streams completions from a local (or remote) Ollama instance.
    /// Ollama uses newline-delimited JSON (NDJSON), not SSE.
    /// </summary>
    public sealed class OllamaLlmService : ILlmService, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string     _model;
        private readonly string     _baseUrl;

        public OllamaLlmService(string model, string baseUrl = "http://localhost:11434")
        {
            _model   = model;
            _baseUrl = baseUrl.TrimEnd('/');
            _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_model))
            {
                reason = "Model name is not set. Enter the Ollama model name in Settings.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public async IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var msgList = new List<object>(messages.Count);
            foreach (var m in messages)
                msgList.Add(new
                {
                    role    = RoleString(m.Role),
                    content = m.Content
                });

            var body = JsonSerializer.Serialize(new
            {
                model    = _model,
                stream   = true,
                messages = msgList
            });

            using var request = new HttpRequestMessage(
                HttpMethod.Post, _baseUrl + "/api/chat");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Ollama error {(int)response.StatusCode}: {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);

            // Ollama streams NDJSON: one JSON object per line
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string? chunk = null;
                bool done = false;
                try
                {
                    using var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                        done = true;

                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content))
                        chunk = content.GetString();
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(chunk))
                    yield return chunk;

                if (done) yield break;
            }
        }

        private static string RoleString(ChatRole role) => role switch
        {
            ChatRole.System    => "system",
            ChatRole.User      => "user",
            ChatRole.Assistant => "assistant",
            _                  => "user"
        };

        public void Dispose() => _http.Dispose();
    }
}
