namespace DynamoCopilot.Core.Services.Rag
{
    internal sealed class RagChunk
    {
        public string ClassName   { get; set; } = string.Empty;
        public string Namespace   { get; set; } = string.Empty;
        // Human-readable text injected into system prompt
        public string DisplayText { get; set; } = string.Empty;
        // Concatenated text for BM25 scoring (camel-split, lower-cased)
        public string IndexText   { get; set; } = string.Empty;
    }
}
