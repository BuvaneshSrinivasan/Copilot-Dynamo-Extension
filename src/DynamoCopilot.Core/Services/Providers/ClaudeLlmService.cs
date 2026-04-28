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
    /// Streams completions from the Anthropic Claude API.
    /// </summary>
    public sealed class ClaudeLlmService : ILlmService, IDisposable
    {
        private const string Endpoint        = "https://api.anthropic.com/v1/messages";
        private const string AnthropicVersion = "2023-06-01";

        private readonly HttpClient _http;
        private readonly string     _apiKey;
        private readonly string     _model;

        public ClaudeLlmService(string apiKey, string model)
        {
            _apiKey = apiKey;
            _model  = model;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                reason = "API key is not set. Enter your Anthropic key in Settings.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_model))
            {
                reason = "Model name is not set. Enter a model in Settings.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public async IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string? systemText = null;
            var turns = new List<Dictionary<string, string>>();

            foreach (var m in messages)
            {
                if (m.Role == ChatRole.System)
                {
                    systemText = m.Content;
                    continue;
                }
                turns.Add(new Dictionary<string, string>
                {
                    ["role"]    = m.Role == ChatRole.User ? "user" : "assistant",
                    ["content"] = m.Content
                });
            }

            var bodyDict = new Dictionary<string, object>
            {
                ["model"]      = _model,
                ["max_tokens"] = 8192,
                ["stream"]     = true,
                ["messages"]   = turns
            };
            if (systemText != null)
                bodyDict["system"] = systemText;

            var body = JsonSerializer.Serialize(bodyDict);

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Claude error {(int)response.StatusCode}: {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line.Substring("data: ".Length);

                string? chunk = null;
                try
                {
                    using var doc  = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();

                        if (type == "message_stop") yield break;

                        if (type == "content_block_delta" &&
                            root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("type", out var deltaType) &&
                            deltaType.GetString() == "text_delta" &&
                            delta.TryGetProperty("text", out var textEl))
                        {
                            chunk = textEl.GetString();
                        }
                    }
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(chunk))
                    yield return chunk;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
