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
    /// Per-provider model + API key pair (used for all providers except Ollama).
    /// </summary>
    public sealed class ProviderConfig
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Ollama-specific config: no API key, just the model name and base URL.
    /// </summary>
    public sealed class OllamaConfig
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "llama3.2";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "http://localhost:11434";
    }

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
            "https://copilot-dynamo-extension-production.up.railway.app";
            
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
        public AiProvider AiProvider { get; set; } = AiProvider.Gemini;

        /// <summary>
        /// Per-provider config (model + API key) keyed by provider name string
        /// (e.g. "OpenAI", "Gemini"). Ollama is excluded — use <see cref="Ollama"/> instead.
        /// Never logged or transmitted to our server.
        /// </summary>
        [JsonPropertyName("providers")]
        public Dictionary<string, ProviderConfig> Providers { get; set; } = new();

        /// <summary>
        /// Ollama-specific config: last-used model name and base URL. No API key.
        /// </summary>
        [JsonPropertyName("ollama")]
        public OllamaConfig Ollama { get; set; } = new();

        // ── Chat behaviour ────────────────────────────────────────────────────

        [JsonPropertyName("maxHistoryTokens")]
        public int MaxHistoryTokens { get; set; } = 20_000;

        /// <summary>Legacy field kept for JSON round-trip compatibility.</summary>
        [JsonPropertyName("maxHistoryMessages")]
        public int MaxHistoryMessages { get; set; } = 40;

        // ── Feature flags ─────────────────────────────────────────────────────

        [JsonPropertyName("enableRag")]
        public bool EnableRag { get; set; } = true;

        [JsonPropertyName("revitApiXmlPath")]
        public string RevitApiXmlPath { get; set; } = string.Empty;

        [JsonPropertyName("enableCodeValidation")]
        public bool EnableCodeValidation { get; set; } = true;

        [JsonPropertyName("enableSpecFirst")]
        public bool EnableSpecFirst { get; set; } = true;

        // ── Per-provider helpers ──────────────────────────────────────────────

        public ProviderConfig GetProvider(AiProvider provider)
        {
            if (provider == AiProvider.Ollama)
                throw new InvalidOperationException("Use the Ollama property for Ollama config.");
            return Providers.TryGetValue(provider.ToString(), out var cfg) ? cfg : new ProviderConfig();
        }

        public void SetProvider(AiProvider provider, string model, string apiKey)
        {
            if (provider == AiProvider.Ollama)
                throw new InvalidOperationException("Use the Ollama property for Ollama config.");
            Providers[provider.ToString()] = new ProviderConfig { Model = model, ApiKey = apiKey };
        }

        public string GetApiKey(AiProvider provider) =>
            provider == AiProvider.Ollama ? string.Empty : GetProvider(provider).ApiKey;

        public string GetModel(AiProvider provider) =>
            provider == AiProvider.Ollama
                ? (string.IsNullOrWhiteSpace(Ollama.Model) ? DefaultModelFor(provider) : Ollama.Model)
                : (string.IsNullOrWhiteSpace(GetProvider(provider).Model) ? DefaultModelFor(provider) : GetProvider(provider).Model);

        // ── Helpers ───────────────────────────────────────────────────────────

        public static string DefaultModelFor(AiProvider provider) => provider switch
        {
            AiProvider.OpenAI   => "gpt-4o",
            AiProvider.Gemini   => "gemini-3-flash-preview",
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
                var fileBytes = File.ReadAllBytes(SettingsFilePath);
                bool isLegacy;
                string json;

                if (Services.SecureStorage.TryDecrypt(fileBytes, out var decrypted))
                {
                    json     = decrypted!;
                    isLegacy = false;
                }
                else
                {
                    // Legacy plaintext file — read as UTF-8 and migrate on save
                    json     = Encoding.UTF8.GetString(fileBytes);
                    isLegacy = true;
                }

                // Deserialize via a shim that handles the legacy flat format
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var settings = JsonSerializer.Deserialize<DynamoCopilotSettings>(json)
                    ?? new DynamoCopilotSettings();

                // ── Migrate legacy flat fields ────────────────────────────────
                // Legacy: { "apiKeys": { "Gemini": "key" }, "modelName": "...", "ollamaUrl": "..." }
                if (root.TryGetProperty("apiKeys", out var legacyKeysEl) &&
                    legacyKeysEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in legacyKeysEl.EnumerateObject())
                    {
                        var name = kv.Name;
                        var key  = kv.Value.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (!settings.Providers.ContainsKey(name))
                            settings.Providers[name] = new ProviderConfig { ApiKey = key };
                        else if (string.IsNullOrWhiteSpace(settings.Providers[name].ApiKey))
                            settings.Providers[name].ApiKey = key;
                    }
                }

                if (root.TryGetProperty("apiKey", out var legacyKeyEl))
                {
                    var legacyKey = legacyKeyEl.GetString() ?? string.Empty;
                    var provName  = settings.AiProvider.ToString();
                    if (!string.IsNullOrWhiteSpace(legacyKey) &&
                        settings.AiProvider != AiProvider.Ollama &&
                        (!settings.Providers.TryGetValue(provName, out var existing) ||
                         string.IsNullOrWhiteSpace(existing.ApiKey)))
                    {
                        if (!settings.Providers.ContainsKey(provName))
                            settings.Providers[provName] = new ProviderConfig();
                        settings.Providers[provName].ApiKey = legacyKey;
                    }
                }

                if (root.TryGetProperty("modelName", out var legacyModelEl))
                {
                    var legacyModel = legacyModelEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(legacyModel))
                    {
                        var provName = settings.AiProvider.ToString();
                        if (settings.AiProvider == AiProvider.Ollama)
                        {
                            if (string.IsNullOrWhiteSpace(settings.Ollama.Model))
                                settings.Ollama.Model = legacyModel;
                        }
                        else
                        {
                            if (!settings.Providers.ContainsKey(provName))
                                settings.Providers[provName] = new ProviderConfig();
                            if (string.IsNullOrWhiteSpace(settings.Providers[provName].Model))
                                settings.Providers[provName].Model = legacyModel;
                        }
                    }
                }

                if (root.TryGetProperty("ollamaUrl", out var legacyOllamaUrlEl))
                {
                    var legacyUrl = legacyOllamaUrlEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(legacyUrl) &&
                        settings.Ollama.Url == "http://localhost:11434")
                        settings.Ollama.Url = legacyUrl;
                }

                // Migrate legacy plaintext file to encrypted format on first load
                if (isLegacy)
                    settings.Save();

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
            File.WriteAllBytes(SettingsFilePath, Services.SecureStorage.Encrypt(json));
        }
    }
}
