using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Calls the Google Gemini API (generateContent streaming endpoint).
    /// Different request/response format from OpenAI — uses SSE with JSON candidates.
    /// </summary>
    public sealed class GeminiLlmService : ILlmService, IDisposable
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;

        public GeminiLlmService(string apiKey, string model)
        {
            _apiKey = apiKey ?? string.Empty;
            _model = model;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public bool IsConfigured(out string reason)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                reason = "Gemini API key not configured. Open the settings panel (⚙) to add your key.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public async IAsyncEnumerable<string> SendStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(out string reason))
                throw new InvalidOperationException(reason);

            var endpoint = $"{BaseUrl}/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";

            // Gemini separates system instructions from conversation
            string? systemText = null;
            var contents = new List<object>();

            foreach (var msg in messages)
            {
                if (msg.Role == ChatRole.System)
                {
                    systemText = msg.Content;
                }
                else
                {
                    contents.Add(new
                    {
                        role = msg.Role == ChatRole.User ? "user" : "model",
                        parts = new[] { new { text = msg.Content } }
                    });
                }
            }

            var requestDict = new Dictionary<string, object>
            {
                ["contents"] = contents,
                ["generationConfig"] = new { temperature = 0.2, maxOutputTokens = 4096 }
            };

            if (systemText != null)
                requestDict["system_instruction"] = new { parts = new[] { new { text = systemText } } };

            var json = JsonSerializer.Serialize(requestDict);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"Gemini API error {(int)response.StatusCode}: {errorBody}");
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line.Substring("data: ".Length);
                string? token = ExtractGeminiText(data);
                if (token != null) yield return token;
            }
        }

        private static string? ExtractGeminiText(string sseData)
        {
            try
            {
                using var doc = JsonDocument.Parse(sseData);
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates)) return null;
                if (candidates.GetArrayLength() == 0) return null;
                if (!candidates[0].TryGetProperty("content", out var content)) return null;
                if (!content.TryGetProperty("parts", out var parts)) return null;
                if (parts.GetArrayLength() == 0) return null;
                if (parts[0].TryGetProperty("text", out var text))
                    return text.GetString();
            }
            catch (JsonException) { }
            return null;
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
