using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DynamoCopilot.Core.Models
{
    public sealed class SpecInput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public sealed class SpecOutput
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;
    }

    public sealed class ClarifyingQuestion
    {
        [JsonPropertyName("question")]
        public string Question { get; set; } = string.Empty;
        [JsonPropertyName("options")]
        public List<string> Options { get; set; } = new List<string>();

        // Runtime only — not serialized
        [JsonIgnore]
        public string Answer { get; set; } = string.Empty;
    }

    public sealed class CodeSpecification
    {
        [JsonPropertyName("inputs")]
        public List<SpecInput> Inputs { get; set; } = new List<SpecInput>();

        [JsonPropertyName("steps")]
        public List<string> Steps { get; set; } = new List<string>();

        [JsonPropertyName("output")]
        public SpecOutput Output { get; set; } = new SpecOutput();

        [JsonPropertyName("questions")]
        public List<ClarifyingQuestion> Questions { get; set; } = new List<ClarifyingQuestion>();
    }

    /// <summary>Result of the spec-generator classification call.</summary>
    public sealed class SpecClassificationResult
    {
        /// <summary>True when the user is requesting code generation (spec card should be shown).</summary>
        public bool IsSpec { get; set; }
        /// <summary>Populated when IsSpec = true.</summary>
        public CodeSpecification? Spec { get; set; }
        /// <summary>Populated when IsSpec = false — the AI's conversational reply.</summary>
        public string? ChatText { get; set; }
    }
}
