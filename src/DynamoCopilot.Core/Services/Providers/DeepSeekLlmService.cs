using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Services.Providers
{
    /// <summary>
    /// DeepSeek uses the OpenAI-compatible API format.
    /// This class is a thin wrapper that points OpenAiLlmService at DeepSeek's endpoint.
    /// </summary>
    public sealed class DeepSeekLlmService : OpenAiLlmService
    {
        private const string DeepSeekBaseUrl = "https://api.deepseek.com";

        public DeepSeekLlmService(string apiKey, string model)
            : base(apiKey, model, DeepSeekBaseUrl)
        {
        }
    }
}
