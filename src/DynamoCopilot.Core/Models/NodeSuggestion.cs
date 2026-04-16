using System;

namespace DynamoCopilot.Core.Models
{
    /// <summary>
    /// A single Dynamo node suggestion returned by POST /api/nodes/suggest.
    /// Mirrors <c>NodeSuggestionWithReason</c> on the server — all fields are
    /// populated when the Gemini re-ranker succeeds; <see cref="Reason"/> is
    /// empty when the fallback path was taken.
    /// </summary>
    public sealed class NodeSuggestion
    {
        public string   Name        { get; set; } = string.Empty;
        public string?  Category    { get; set; }
        public string   PackageName { get; set; } = string.Empty;
        public string?  Description { get; set; }
        public string[] InputPorts  { get; set; } = Array.Empty<string>();
        public string[] OutputPorts { get; set; } = Array.Empty<string>();

        /// <summary>Cosine similarity score in [0, 1] — higher is more relevant.</summary>
        public float Score { get; set; }

        /// <summary>
        /// Gemini's one-sentence explanation of why this node fits the user's goal.
        /// Empty when the re-ranker fell back due to a Gemini API error.
        /// </summary>
        public string Reason { get; set; } = string.Empty;
    }
}
