using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Settings
{
    public enum AiProvider
    {
        Groq = 0,        // Free tier - fast Llama 3 models
        Gemini = 1,      // Free tier - Google Gemini models
        OpenRouter = 2,  // Many free models available
        Ollama = 3,      // Local, completely free
        OpenAI = 4       // Paid - GPT-4o
    }

    /// <summary>
    /// User-level settings for DynamoCopilot.
    /// Persisted to: %APPDATA%\DynamoCopilot\settings.json
    /// </summary>
    public sealed class DynamoCopilotSettings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamoCopilot",
            "settings.json");

        [JsonPropertyName("provider")]
        public AiProvider Provider { get; set; } = AiProvider.Groq;

        // ── OpenAI ──────────────────────────────────────────────────────
        [JsonPropertyName("openAiApiKey")]
        public string OpenAiApiKey { get; set; } = string.Empty;

        [JsonPropertyName("openAiModel")]
        public string OpenAiModel { get; set; } = "gpt-4o";

        // ── Groq (free tier) ─────────────────────────────────────────────
        [JsonPropertyName("groqApiKey")]
        public string GroqApiKey { get; set; } = string.Empty;

        [JsonPropertyName("groqModel")]
        public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

        // ── Gemini (free tier) ───────────────────────────────────────────
        [JsonPropertyName("geminiApiKey")]
        public string GeminiApiKey { get; set; } = string.Empty;

        [JsonPropertyName("geminiModel")]
        public string GeminiModel { get; set; } = "gemini-2.0-flash";

        // ── OpenRouter (free models available) ───────────────────────────
        [JsonPropertyName("openRouterApiKey")]
        public string OpenRouterApiKey { get; set; } = string.Empty;

        [JsonPropertyName("openRouterModel")]
        public string OpenRouterModel { get; set; } = "meta-llama/llama-3.3-70b-instruct:free";

        // ── Ollama (local, no API key needed) ────────────────────────────
        [JsonPropertyName("ollamaEndpoint")]
        public string OllamaEndpoint { get; set; } = "http://localhost:11434";

        [JsonPropertyName("ollamaModel")]
        public string OllamaModel { get; set; } = "llama3";

        [JsonPropertyName("maxHistoryMessages")]
        public int MaxHistoryMessages { get; set; } = 40;

        /// <summary>
        /// Loads settings from disk. Returns defaults if file does not exist.
        /// </summary>
        public static DynamoCopilotSettings Load()
        {
            if (!File.Exists(SettingsFilePath))
                return new DynamoCopilotSettings();

            try
            {
                var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<DynamoCopilotSettings>(json)
                    ?? new DynamoCopilotSettings();
            }
            catch
            {
                return new DynamoCopilotSettings();
            }
        }

        /// <summary>Saves current settings to disk.</summary>
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
        }
    }
}
