using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AiProvider
    {
        OpenAI,
        Gemini,
        Claude,
        DeepSeek,
        Ollama
    }
}
