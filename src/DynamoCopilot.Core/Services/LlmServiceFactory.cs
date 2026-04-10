using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Core.Services
{
    public static class LlmServiceFactory
    {
        public static ILlmService Create(DynamoCopilotSettings settings)
        {
            switch (settings.Provider)
            {
                case AiProvider.Groq:
                    return new OpenAiLlmService(
                        "https://api.groq.com/openai/v1",
                        settings.GroqApiKey,
                        settings.GroqModel);

                case AiProvider.Gemini:
                    return new GeminiLlmService(
                        settings.GeminiApiKey,
                        settings.GeminiModel);

                case AiProvider.OpenRouter:
                    return new OpenAiLlmService(
                        "https://openrouter.ai/api/v1",
                        settings.OpenRouterApiKey,
                        settings.OpenRouterModel);

                case AiProvider.Ollama:
                    return new OpenAiLlmService(
                        settings.OllamaEndpoint.TrimEnd('/') + "/v1",
                        string.Empty,
                        settings.OllamaModel,
                        requiresKey: false);

                default: // OpenAI
                    return new OpenAiLlmService(
                        "https://api.openai.com/v1",
                        settings.OpenAiApiKey,
                        settings.OpenAiModel);

                case AiProvider.Server:
                    return new ServerLlmService(
                        settings.ServerUrl,
                        settings.ServerAuthToken);
            }
        }
    }
}
