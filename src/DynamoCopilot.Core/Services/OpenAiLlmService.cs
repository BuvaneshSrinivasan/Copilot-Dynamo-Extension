using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Calls any OpenAI-compatible Chat Completions endpoint with streaming.
    /// Works for OpenAI, Groq, OpenRouter, and Ollama.
    /// </summary>
    public sealed class OpenAiLlmService : ILlmService, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly bool _requiresKey;

        public OpenAiLlmService(string baseUrl, string apiKey, string model, bool requiresKey = true)
        {
            _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
            _apiKey = apiKey ?? string.Empty;
            _model = model;
            _requiresKey = requiresKey;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public bool IsConfigured(out string reason)
        {
            if (_requiresKey && string.IsNullOrWhiteSpace(_apiKey))
            {
                reason = "API key is not configured. Open the settings panel (âš™) to add your key.";
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

            var requestBody = BuildRequestBody(messages);
            var json = JsonSerializer.Serialize(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            if (!string.IsNullOrWhiteSpace(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException(
                    $"API error {(int)response.StatusCode}: {errorBody}");
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line.Substring("data: ".Length);
                if (data == "[DONE]") yield break;

                string? token = ExtractDeltaContent(data);
                if (token != null) yield return token;
            }
        }

        private static string? ExtractDeltaContent(string sseData)
        {
            try
            {
                using var doc = JsonDocument.Parse(sseData);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) return null;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                    return content.GetString();
            }
            catch (JsonException) { }
            return null;
        }

        private object BuildRequestBody(IReadOnlyList<ChatMessage> messages)
        {
            var msgArray = new List<object>(messages.Count);
            foreach (var m in messages)
            {
                msgArray.Add(new
                {
                    role = m.Role.ToString().ToLowerInvariant(),
                    content = m.Content
                });
            }
            return new
            {
                model = _model,
                messages = msgArray,
                stream = true,
                temperature = 0.2,
                max_tokens = 4096
            };
        }

        public void Dispose() => _httpClient.Dispose();
    }
}

