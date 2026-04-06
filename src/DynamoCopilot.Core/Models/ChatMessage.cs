using System;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    /// <summary>Role of a participant in the conversation.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChatRole
    {
        System,
        User,
        Assistant
    }

    /// <summary>A single message in a chat conversation.</summary>
    public sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public ChatRole Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// If the assistant message contains a generated Python code block,
        /// the extracted code is stored here for easy injection into Dynamo.
        /// </summary>
        [JsonPropertyName("codeSnippet")]
        public string? CodeSnippet { get; set; }
    }
}
