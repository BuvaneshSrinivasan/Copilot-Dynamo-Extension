using System;

namespace DynamoCopilot.Core.Services.Providers
{
    /// <summary>
    /// Thrown when the LLM provider returns HTTP 429 (rate limited) instead of the generic
    /// <see cref="System.Net.Http.HttpRequestException"/>. Carries a human-readable message and,
    /// when the provider supplies one, a retry-after hint — so callers can show a clean message
    /// instead of dumping the raw provider error JSON (which, for OpenRouter's multi-model
    /// fallback chain, includes a "previous_errors" array from every model that was tried).
    /// </summary>
    public sealed class LlmRateLimitException : Exception
    {
        /// <summary>Seconds to wait before retrying, if the provider supplied one.</summary>
        public double? RetryAfterSeconds { get; }

        public LlmRateLimitException(string message, double? retryAfterSeconds)
            : base(message)
        {
            RetryAfterSeconds = retryAfterSeconds;
        }
    }
}
