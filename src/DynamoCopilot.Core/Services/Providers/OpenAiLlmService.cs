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
        /// <summary>
        /// Caps worst-case generation length (matches ClaudeLlmService's cap). This does not
        /// affect the quality of tokens generated before the cap — it only stops a response
        /// that would otherwise ramble on indefinitely (seen with some free-tier models ignoring
        /// the system prompt's brevity instructions). 8192 tokens is generous headroom over any
        /// realistic Dynamo script + explanation.
        /// </summary>
        protected const int MaxTokens = 8192;

        private readonly HttpClient _http;
        private readonly string     _apiKey;
        protected readonly string   _model;
        private readonly string     _baseUrl;

        /// <summary>
        /// True if the most recent <see cref="SendStreamingAsync"/> call was cut off by
        /// <see cref="MaxTokens"/> (finish_reason == "length") rather than finishing naturally.
        /// Reset at the start of every call.
        /// </summary>
        public bool WasTruncated { get; private set; }

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
            WasTruncated = false;
            var msgArray = BuildMessages(messages);
            var body = JsonSerializer.Serialize(BuildRequestBody(msgArray));

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
                if ((int)response.StatusCode == 429) // HttpStatusCode.TooManyRequests — not defined on net48
                    throw BuildRateLimitException(err);
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

                    var choice = choices[0];
                    if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                        finishReason.ValueKind == JsonValueKind.String &&
                        finishReason.GetString() == "length")
                        WasTruncated = true;

                    var delta = choice.GetProperty("delta");
                    if (delta.TryGetProperty("content", out var content))
                        chunk = content.GetString();
                }
                catch (JsonException) { }

                if (!string.IsNullOrEmpty(chunk))
                    yield return chunk;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a 429 error body into a clean, human-readable message + retry-after hint.
        /// Handles both plain OpenAI-style bodies (<c>error.message</c>) and OpenRouter's
        /// richer shape (<c>error.metadata.raw</c> / <c>error.metadata.retry_after_seconds</c>,
        /// which also lists every model tried in its fallback chain under "previous_errors").
        /// Falls back to a generic message if the body doesn't match either shape.
        /// </summary>
        private static LlmRateLimitException BuildRateLimitException(string errorBody)
        {
            string message = "The model provider is temporarily rate-limited. Please try again shortly.";
            double? retryAfter = null;
            try
            {
                using var doc = JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("metadata", out var metadata))
                    {
                        if (metadata.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
                            message = raw.GetString() ?? message;
                        if (metadata.TryGetProperty("retry_after_seconds", out var retry) && retry.TryGetDouble(out var r))
                            retryAfter = r;
                    }
                    else if (error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        message = msg.GetString() ?? message;
                    }
                }
            }
            catch (JsonException) { }

            return new LlmRateLimitException(message, retryAfter);
        }

        /// <summary>
        /// Builds the JSON request body. Overridden by providers that need a
        /// different shape (e.g. OpenRouter's "models" fallback array).
        /// </summary>
        protected virtual Dictionary<string, object> BuildRequestBody(
            List<Dictionary<string, string>> msgArray) => new()
        {
            ["model"]      = _model,
            ["max_tokens"] = MaxTokens,
            ["stream"]     = true,
            ["messages"]   = msgArray
        };

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
