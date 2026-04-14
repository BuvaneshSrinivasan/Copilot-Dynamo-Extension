namespace DynamoCopilot.NodeIndexer.Models;

// =============================================================================
// NodeRecord — one extracted node ready to embed and store
// =============================================================================
// Produced by PackageExtractor / DyfParser and consumed by GeminiEmbedder
// and NodeRepository. Immutable after extraction.
// =============================================================================

public sealed class NodeRecord
{
    // ── IDENTITY ──────────────────────────────────────────────────────────────
    public required string Name { get; init; }
    public required string PackageName { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }

    // ── PACKAGE CONTEXT ───────────────────────────────────────────────────────
    public string? PackageDescription { get; init; }
    public string[] Keywords { get; init; } = [];

    // ── PORT METADATA ─────────────────────────────────────────────────────────
    // e.g. ["keys:var[]", "values:var[][]"] from Symbol nodes in DYF files
    public string[] InputPorts { get; init; } = [];
    public string[] OutputPorts { get; init; } = [];

    // ── SOURCE ────────────────────────────────────────────────────────────────
    // "DYF" | "ZeroTouch" | "XmlDoc"
    public string NodeType { get; init; } = "DYF";

    // ── EMBEDDING TEXT ────────────────────────────────────────────────────────
    // The concatenated text sent to the embedding model.
    // Built once by BuildEmbeddingText() and cached here.
    public string EmbeddingText => BuildEmbeddingText();

    private string BuildEmbeddingText()
    {
        var parts = new List<string>();

        parts.Add($"Node: {Name}");

        if (!string.IsNullOrWhiteSpace(Description))
            parts.Add($"Description: {Description}");

        if (!string.IsNullOrWhiteSpace(Category))
            parts.Add($"Category: {Category}");

        if (!string.IsNullOrWhiteSpace(PackageName))
            parts.Add($"Package: {PackageName}");

        if (!string.IsNullOrWhiteSpace(PackageDescription))
            parts.Add($"Package description: {PackageDescription}");

        if (InputPorts.Length > 0)
            parts.Add($"Inputs: {string.Join(", ", InputPorts)}");

        if (OutputPorts.Length > 0)
            parts.Add($"Outputs: {string.Join(", ", OutputPorts)}");

        if (Keywords.Length > 0)
            parts.Add($"Keywords: {string.Join(", ", Keywords)}");

        return string.Join(". ", parts);
    }
}
