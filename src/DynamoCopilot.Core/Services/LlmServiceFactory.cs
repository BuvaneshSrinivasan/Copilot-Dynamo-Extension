using System;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Services.Providers;
using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Creates an <see cref="ILlmService"/> from the current user settings.
    /// Call <see cref="Create"/> whenever settings change — the returned instance
    /// is not updated dynamically.
    /// </summary>
    public static class LlmServiceFactory
    {
        public static ILlmService Create(DynamoCopilotSettings settings)
        {
            var key   = settings.GetApiKey(settings.AiProvider);
            var model = settings.GetModel(settings.AiProvider);

            return settings.AiProvider switch
            {
                AiProvider.OpenAI   => new OpenAiLlmService(key, model),
                AiProvider.Gemini   => new GeminiLlmService(key, model),
                AiProvider.Claude   => new ClaudeLlmService(key, model),
                AiProvider.DeepSeek => new DeepSeekLlmService(key, model),
                AiProvider.Ollama   => new OllamaLlmService(model, settings.Ollama.Url),
                _                   => throw new NotSupportedException(
                                           $"Unknown provider: {settings.AiProvider}")
            };
        }
    }
}
