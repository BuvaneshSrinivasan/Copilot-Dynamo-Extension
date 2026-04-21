using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamoCopilot.Core.Models;

namespace DynamoCopilot.Core.Settings
{
    /// <summary>
    /// Application-level settings persisted to %AppData%\DynamoCopilot\settings.json.
    /// </summary>
    public sealed class DynamoCopilotSettings
    {
        public static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamoCopilot");

        private static readonly string SettingsFilePath =
            Path.Combine(AppDataDir, "settings.json");

        // ── Auth server (licensing only) ──────────────────────────────────────

        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; set; } =
            "https://radiant-determination-production.up.railway.app";

        [JsonPropertyName("useLocalServer")]
        public bool UseLocalServer { get; set; } = false;

        [JsonPropertyName("localServerUrl")]
        public string LocalServerUrl { get; set; } = "http://localhost:8080";

        [JsonIgnore]
        public string EffectiveServerUrl => UseLocalServer
            ? LocalServerUrl.TrimEnd('/')
            : ServerUrl.TrimEnd('/');

        // ── AI provider (BYOK) ────────────────────────────────────────────────

        [JsonPropertyName("aiProvider")]
        public AiProvider AiProvider { get; set; } = AiProvider.OpenAI;

        /// <summary>API key for the selected provider. Never logged or transmitted to our server.</summary>
        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Model name exactly as the provider expects it (e.g. "gpt-4o", "gemini-2.5-flash").</summary>
        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        // ── Ollama-specific ───────────────────────────────────────────────────

        /// <summary>Base URL for a local or remote Ollama instance.</summary>
        [JsonPropertyName("ollamaUrl")]
        public string OllamaUrl { get; set; } = "http://localhost:11434";

        // ── Chat behaviour ────────────────────────────────────────────────────

        [JsonPropertyName("maxHistoryMessages")]
        public int MaxHistoryMessages { get; set; } = 40;

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Default model name shown as placeholder for each provider.</summary>
        public static string DefaultModelFor(AiProvider provider) => provider switch
        {
            AiProvider.OpenAI   => "gpt-4o",
            AiProvider.Gemini   => "gemini-2.5-flash",
            AiProvider.Claude   => "claude-sonnet-4-6",
            AiProvider.DeepSeek => "deepseek-chat",
            AiProvider.Ollama   => "llama3.2",
            _                   => string.Empty
        };

        public static DynamoCopilotSettings Load()
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = new DynamoCopilotSettings();
                defaults.Save();
                return defaults;
            }

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

        public void Save()
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
        }
    }
}
