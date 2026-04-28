using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Extractors;

// =============================================================================
// PackageExtractor — Reads a Dynamo package zip and yields NodeRecords
// =============================================================================
// Extraction strategy (in order of richness):
//   1. pkg.json          → package name, description, keywords (always present)
//   2. dyf/*.dyf         → individual node name/desc/category/ports (XML files)
//   3. bin/*.xml doc     → XML documentation for ZeroTouch nodes (when available)
//
// DLL reflection is deliberately skipped in Phase A — MetadataLoadContext would
// give us ZeroTouch node names but at the cost of complexity and failure rate.
// The pkg.json + DYF path covers the vast majority of community packages.
// =============================================================================

public static class PackageExtractor
{
    public static IReadOnlyList<NodeRecord> ExtractFromZip(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return ExtractFromArchive(archive);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Extracts node records from an already-unpacked package folder.
    /// Expects the standard Dynamo package layout: pkg.json + dyf/ + bin/.
    /// </summary>
    public static IReadOnlyList<NodeRecord> ExtractFromDirectory(string packageDir)
    {
        try
        {
            var pkgJsonPath = Path.Combine(packageDir, "pkg.json");
            if (!File.Exists(pkgJsonPath)) return [];

            var pkg = ReadPkgJsonFromFile(pkgJsonPath);
            if (pkg.Name == null) return [];

            var nodes = new List<NodeRecord>();

            // ── DYF nodes ─────────────────────────────────────────────────────
            var dyfDir = Path.Combine(packageDir, "dyf");
            if (Directory.Exists(dyfDir))
            {
                foreach (var dyfFile in Directory.EnumerateFiles(dyfDir, "*.dyf"))
                {
                    var xml = ReadFileAsString(dyfFile);
                    if (xml == null) continue;
                    var node = DyfParser.Parse(xml, pkg.Name, pkg.Description ?? "", pkg.Keywords);
                    if (node != null) nodes.Add(node);
                }
            }

            // ── ZeroTouch XML doc nodes ───────────────────────────────────────
            var binDir = Path.Combine(packageDir, "bin");
            if (Directory.Exists(binDir))
            {
                foreach (var xmlFile in Directory.EnumerateFiles(binDir, "*.xml"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(xmlFile);
                    if (!IsNodeLibraryXml(pkg.NodeLibraries, baseName)) continue;
                    var xml = ReadFileAsString(xmlFile);
                    if (xml == null) continue;
                    var xmlNodes = XmlDocParser.Parse(xml, pkg.Name, pkg.Description ?? "", pkg.Keywords);
                    nodes.AddRange(xmlNodes);
                }
            }

            return nodes;
        }
        catch { return []; }
    }

    private static IReadOnlyList<NodeRecord> ExtractFromArchive(ZipArchive archive)
    {
        // ── 1. READ pkg.json ──────────────────────────────────────────────────
        var pkgEntry = archive.Entries
            .FirstOrDefault(e => e.FullName.Equals("pkg.json", StringComparison.OrdinalIgnoreCase)
                              || e.Name.Equals("pkg.json", StringComparison.OrdinalIgnoreCase));

        if (pkgEntry == null) return [];

        var pkg = ReadPkgJson(pkgEntry);
        if (pkg.Name == null) return [];

        // ── 2. EXTRACT DYF NODES ──────────────────────────────────────────────
        var nodes = new List<NodeRecord>();

        var dyfEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".dyf", StringComparison.OrdinalIgnoreCase));

        foreach (var dyfEntry in dyfEntries)
        {
            var xml = ReadEntryAsString(dyfEntry);
            if (xml == null) continue;

            var node = DyfParser.Parse(xml, pkg.Name, pkg.Description ?? "", pkg.Keywords);
            if (node != null)
                nodes.Add(node);
        }

        // ── 3. EXTRACT ZEROTOUGH NODES FROM XML DOCS ─────────────────────────
        // XML doc files sit alongside DLLs in bin/ and have the same name as the
        // DLL but with a .xml extension. They carry <member> elements with
        // <summary>, <param>, and <returns> for each public method.
        var xmlDocEntries = archive.Entries
            .Where(e =>
                e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                (e.FullName.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                 e.FullName.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)));

        foreach (var xmlEntry in xmlDocEntries)
        {
            var baseName = Path.GetFileNameWithoutExtension(xmlEntry.Name);
            if (!IsNodeLibraryXml(pkg.NodeLibraries, baseName)) continue;
            var xml = ReadEntryAsString(xmlEntry);
            if (xml == null) continue;

            var xmlNodes = XmlDocParser.Parse(xml, pkg.Name, pkg.Description ?? "", pkg.Keywords);
            nodes.AddRange(xmlNodes);
        }

        return nodes;
    }

    private static PkgInfo ReadPkgJson(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var name        = GetString(root, "name");
            var description = GetString(root, "description");
            var keywords    = root.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
                ? kw.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString() ?? "")
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToArray()
                : [];

            // node_libraries absent → null (legacy; treat all DLLs as node libraries, matching Dynamo behaviour)
            // node_libraries present but empty → empty array (package has no node libraries)
            string[]? nodeLibraries = null;
            if (root.TryGetProperty("node_libraries", out var nl) && nl.ValueKind == JsonValueKind.Array)
                nodeLibraries = nl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .ToArray();

            return new PkgInfo(name, description, keywords, nodeLibraries);
        }
        catch
        {
            return new PkgInfo(null, null, [], null);
        }
    }

    private static string? ReadEntryAsString(ZipArchiveEntry entry)
    {
        // Skip large entries (> 2MB) — not useful for indexing
        if (entry.Length > 2 * 1024 * 1024) return null;

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static PkgInfo ReadPkgJsonFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc    = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var name        = GetString(root, "name");
            var description = GetString(root, "description");
            var keywords    = root.TryGetProperty("keywords", out var kw) &&
                              kw.ValueKind == JsonValueKind.Array
                ? kw.EnumerateArray()
                    .Where(k  => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString() ?? "")
                    .Where(k  => !string.IsNullOrWhiteSpace(k))
                    .ToArray()
                : [];

            string[]? nodeLibraries = null;
            if (root.TryGetProperty("node_libraries", out var nl) && nl.ValueKind == JsonValueKind.Array)
                nodeLibraries = nl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .ToArray();

            return new PkgInfo(name, description, keywords, nodeLibraries);
        }
        catch { return new PkgInfo(null, null, [], null); }
    }

    private static string? ReadFileAsString(string filePath)
    {
        const long maxBytes = 2 * 1024 * 1024;
        try
        {
            if (new FileInfo(filePath).Length > maxBytes) return null;
            return File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch { return null; }
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Returns true when an XML doc file should be indexed for this package.
    /// Mirrors Dynamo's Package.IsNodeLibrary logic (Package.cs:459-491):
    ///   - nodeLibraries null  → legacy package, index everything
    ///   - nodeLibraries empty → package has no node libraries, skip all
    ///   - otherwise           → only index if the DLL simple name is listed
    /// The xmlFileBaseName is the filename without extension (e.g. "LunchBox").
    /// Each entry in nodeLibraries is an AssemblyFullName string; we match on simple name.
    /// </summary>
    private static bool IsNodeLibraryXml(string[] ? nodeLibraries, string xmlFileBaseName)
    {
        if (nodeLibraries == null) return true;
        foreach (var entry in nodeLibraries)
        {
            // entry format: "AssemblySimpleName, Version=x.x, Culture=..., PublicKeyToken=..."
            var simpleName = entry.Contains(',') ? entry[..entry.IndexOf(',')].Trim() : entry.Trim();
            if (simpleName.Equals(xmlFileBaseName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // node_libraries is the list from pkg.json — only DLLs named here are Dynamo node libraries.
    // Null means the field was absent (legacy packages), which Dynamo treats as "all DLLs are node libraries".
    private sealed record PkgInfo(string? Name, string? Description, string[] Keywords, string[]? NodeLibraries);
}
