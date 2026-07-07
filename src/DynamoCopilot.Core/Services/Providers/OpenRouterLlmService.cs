using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamoCopilot.Core.Services.Providers
{
    /// <summary>
    /// OpenRouter uses the OpenAI-compatible wire format, but instead of a single
    /// "model" string it accepts an ordered "models" array — OpenRouter itself
    /// retries the next model in the list on error, moderation, rate-limiting,
    /// or downtime, all within one HTTP call.
    ///
    /// ModelName for this provider is a comma-separated ordered list
    /// (e.g. "qwen/qwen3-coder:free,poolside/laguna-m.1:free") rather than a
    /// single model ID.
    /// </summary>
    public sealed class OpenRouterLlmService : OpenAiLlmService
    {
        private const string OpenRouterBaseUrl = "https://openrouter.ai/api";

        public OpenRouterLlmService(string apiKey, string modelChain)
            : base(apiKey, modelChain, OpenRouterBaseUrl)
        {
        }

        protected override Dictionary<string, object> BuildRequestBody(
            List<Dictionary<string, string>> msgArray)
        {
            var models = _model
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim())
                .Where(m => m.Length > 0)
                .ToArray();

            return new Dictionary<string, object>
            {
                ["models"]     = models,
                ["max_tokens"] = MaxTokens,
                ["stream"]     = true,
                ["messages"]   = msgArray
            };
        }
    }
}
