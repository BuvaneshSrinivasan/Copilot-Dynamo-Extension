using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Settings
{
    /// <summary>
    /// Application-level settings persisted to %AppData%\DynamoCopilot\settings.json.
    ///
    /// ServerUrl is intentionally not surfaced in the UI — it is a deployment
    /// concern, not a user preference. Edit settings.json directly to point
    /// the extension at a different server without rebuilding.
    /// </summary>
    public sealed class DynamoCopilotSettings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamoCopilot",
            "settings.json");

        /// <summary>DynamoCopilot backend URL. Not shown to users.</summary>
        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; set; } =
            "https://radiant-determination-production.up.railway.app";

        /// <summary>How many past messages to include with each chat request.</summary>
        [JsonPropertyName("maxHistoryMessages")]
        public int MaxHistoryMessages { get; set; } = 40;

        [JsonPropertyName("useLocalServer")]
        public bool UseLocalServer { get; set; } = false;

        [JsonPropertyName("localServerUrl")]
        public string LocalServerUrl { get; set; } = "http://localhost:8080";

        [JsonIgnore]
        public string EffectiveServerUrl => UseLocalServer
            ? LocalServerUrl.TrimEnd('/')
            : ServerUrl.TrimEnd('/');

        public static DynamoCopilotSettings Load()
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = new DynamoCopilotSettings();
                defaults.Save();   // write defaults on first run so the file is visible
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
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
        }
    }
}
