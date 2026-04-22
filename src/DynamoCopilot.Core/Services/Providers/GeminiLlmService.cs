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
    /// Streams completions from the Google Gemini API directly from the client.
    /// </summary>
    public sealed class GeminiLlmService : ILlmService, IDisposable
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        private readonly HttpClient _http;
        private readonly string     _apiKey;
        private readonly string     _model;

        public GeminiLlmService(string apiKey, string model)
        {
            _apiKey = apiKey;
            _model  = model;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                reason = "API key is not set. Enter your Gemini key in Settings.";
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
            var url = $"{BaseUrl}/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";

            // Split system message from conversation turns
            string? systemText = null;
            var contents = new List<object>();

            foreach (var m in messages)
            {
                if (m.Role == ChatRole.System)
                {
                    systemText = m.Content;
                    continue;
                }
                contents.Add(new
                {
                    role  = m.Role == ChatRole.User ? "user" : "model",
                    parts = new[] { new { text = m.Content } }
                });
            }

            var bodyObj = systemText != null
                ? (object)new
                {
                    systemInstruction = new { parts = new[] { new { text = systemText } } },
                    contents,
                    generationConfig  = new { thinkingConfig = new { thinkingBudget = 0 } }
                }
                : new
                {
                    contents,
                    generationConfig = new { thinkingConfig = new { thinkingBudget = 0 } }
                };

            var body = JsonSerializer.Serialize(bodyObj);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content  = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Gemini error {(int)response.StatusCode}: {err}");
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
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("candidates", out var candidates) &&
                        candidates.GetArrayLength() > 0 &&
                        candidates[0].TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textEl))
                    {
                        chunk = textEl.GetString();
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
