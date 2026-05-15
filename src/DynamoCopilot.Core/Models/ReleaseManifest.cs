using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    public sealed class ReleaseManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("minVersion")]
        public string MinVersion { get; set; } = "1.0.0";

        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; set; } = "";

        [JsonPropertyName("dlls")]
        public DllsInfo? Dlls { get; set; }

        [JsonPropertyName("nodesDb")]
        public DbInfo? NodesDb { get; set; }

        public sealed class DllsInfo
        {
            [JsonPropertyName("url")]
            public string Url { get; set; } = "";

            [JsonPropertyName("sizeBytes")]
            public long SizeBytes { get; set; }
        }

        public sealed class DbInfo
        {
            [JsonPropertyName("dbVersion")]
            public string? DbVersion { get; set; }

            [JsonPropertyName("url")]
            public string Url { get; set; } = "";

            [JsonPropertyName("sizeBytes")]
            public long? SizeBytes { get; set; }
        }
    }
}
