using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    /// <summary>
    /// A full conversation session tied to a specific Dynamo graph file.
    /// Persisted to disk between Dynamo sessions.
    /// </summary>
    public sealed class ChatSession
    {
        /// <summary>Absolute path of the .dyn file this session belongs to.</summary>
        [JsonPropertyName("graphFilePath")]
        public string GraphFilePath { get; set; } = string.Empty;

        /// <summary>All messages in the conversation (excludes the hidden system prompt).</summary>
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("lastModifiedAt")]
        public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Python engine last detected for this graph ("IronPython2" or "CPython3").</summary>
        [JsonPropertyName("pythonEngine")]
        public string PythonEngine { get; set; } = "IronPython2";
    }
}
