using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Per-provider API keys keyed by provider name string (e.g. "OpenAI", "Gemini").
        /// Never logged or transmitted to our server.
        /// </summary>
        [JsonPropertyName("apiKeys")]
        public Dictionary<string, string> ApiKeys { get; set; } = new();

        /// <summary>
        /// Legacy single-key field — kept for JSON round-trip compatibility.
        /// Prefer <see cref="ApiKeys"/> for reading/writing keys.
        /// </summary>
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

        // ── Feature flags ─────────────────────────────────────────────────────

        /// <summary>Enable BM25 RAG context injection from local RevitAPI.xml.</summary>
        [JsonPropertyName("enableRag")]
        public bool EnableRag { get; set; } = true;

        /// <summary>Optional override path to the directory containing RevitAPI.xml.</summary>
        [JsonPropertyName("revitApiXmlPath")]
        public string RevitApiXmlPath { get; set; } = string.Empty;

        /// <summary>Enable post-generation Revit enum validation + LLM auto-fix.</summary>
        [JsonPropertyName("enableCodeValidation")]
        public bool EnableCodeValidation { get; set; } = true;

        /// <summary>Enable spec-first workflow (classify → spec card → code gen).</summary>
        [JsonPropertyName("enableSpecFirst")]
        public bool EnableSpecFirst { get; set; } = true;

        // ── Per-provider key helpers ──────────────────────────────────────────

        public string GetApiKey(AiProvider provider) =>
            ApiKeys.TryGetValue(provider.ToString(), out var key) ? key : string.Empty;

        public void SetApiKey(AiProvider provider, string key) =>
            ApiKeys[provider.ToString()] = key;

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
                var settings = JsonSerializer.Deserialize<DynamoCopilotSettings>(json)
                    ?? new DynamoCopilotSettings();

                // Migrate legacy single apiKey into the per-provider dict if not already there
                if (!string.IsNullOrEmpty(settings.ApiKey) &&
                    !settings.ApiKeys.ContainsKey(settings.AiProvider.ToString()))
                {
                    settings.ApiKeys[settings.AiProvider.ToString()] = settings.ApiKey;
                }

                // Keep ApiKey in sync with the active provider
                settings.ApiKey = settings.GetApiKey(settings.AiProvider);

                return settings;
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
