using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services.Providers
{
    /// <summary>
    /// Streams completions from any OpenAI-compatible endpoint.
    /// DeepSeek uses the same wire format — pass its base URL to reuse this class.
    /// </summary>
    public class OpenAiLlmService : ILlmService, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string     _apiKey;
        private readonly string     _model;
        private readonly string     _baseUrl;

        public OpenAiLlmService(string apiKey, string model, string baseUrl = "https://api.openai.com")
        {
            _apiKey  = apiKey;
            _model   = model;
            _baseUrl = baseUrl.TrimEnd('/');
            _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                reason = "API key is not set. Enter your key in Settings.";
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
            var msgArray = BuildMessages(messages);
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["model"]    = _model,
                ["stream"]   = true,
                ["messages"] = msgArray
            });

            using var request = new HttpRequestMessage(
                HttpMethod.Post, _baseUrl + "/v1/chat/completions");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"OpenAI error {(int)response.StatusCode}: {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line.Substring("data: ".Length);
                if (data == "[DONE]") yield break;

                string? chunk = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;

                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var content))
                        chunk = content.GetString();
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(chunk))
                    yield return chunk;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<Dictionary<string, string>> BuildMessages(IReadOnlyList<ChatMessage> messages)
        {
            var list = new List<Dictionary<string, string>>(messages.Count);
            foreach (var m in messages)
                list.Add(new Dictionary<string, string>
                {
                    ["role"]    = RoleString(m.Role),
                    ["content"] = m.Content
                });
            return list;
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
