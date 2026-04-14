using Pgvector;

namespace DynamoCopilot.Server.Models;

// =============================================================================
// DynamoNode — represents one indexed node from the Dynamo package ecosystem
// =============================================================================
// Populated offline by DynamoCopilot.NodeIndexer and queried at runtime by
// NodeSearchService to answer /api/nodes/suggest requests.
// =============================================================================

public class DynamoNode
{
    public Guid Id { get; set; }

    // ── NODE IDENTITY ──────────────────────────────────────────────────────────
    // Name as it appears in Dynamo (e.g. "Springs.Dictionary.ByKeysValues")
    public string Name { get; set; } = "";

    // Human-readable description from the DYF or XML doc comment
    public string? Description { get; set; }

    // Dot-separated namespace path (e.g. "Springs.Core.List.Create")
    public string? Category { get; set; }

    // ── SOURCE PACKAGE ────────────────────────────────────────────────────────
    public string PackageName { get; set; } = "";
    public string? PackageDescription { get; set; }
    public string[]? Keywords { get; set; }

    // ── PORT METADATA ─────────────────────────────────────────────────────────
    // Stored as typed port strings, e.g. ["keys:var[]", "values:var[][]"]
    public string[]? InputPorts { get; set; }
    public string[]? OutputPorts { get; set; }

    // ── SOURCE INFO ───────────────────────────────────────────────────────────
    // "DYF" for custom nodes | "ZeroTouch" for DLL-based nodes | "XmlDoc" for
    // ZeroTouch nodes enriched from their XML documentation file
    public string NodeType { get; set; } = "DYF";

    // ── VECTOR EMBEDDING ──────────────────────────────────────────────────────
    // 768-dimensional embedding from Gemini text-embedding-004.
    // Null until the indexer has processed this row.
    public Vector? Embedding { get; set; }

    public DateTime IndexedAt { get; set; }
}
