using System.Text.Json;
using System.Xml.Linq;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Extractors;

// =============================================================================
// DyfParser — Extracts node metadata from Dynamo custom node (.dyf) files
// =============================================================================
// Dynamo uses two DYF formats:
//
// XML (Dynamo 1.x):
//   <Workspace Name="..." Description="..." Category="...">
//     <Elements>
//       <Dynamo.Graph.Nodes.CustomNodes.Symbol> <Symbol value="keys:var[]" /> </...>
//       <Dynamo.Graph.Nodes.CustomNodes.Output>  <Symbol value="result" />    </...>
//     </Elements>
//   </Workspace>
//
// JSON (Dynamo 2.x):
//   { "Name": "...", "Description": "...", "Category": "...",
//     "Inputs":  [{ "Name": "portName", ... }],
//     "Outputs": [{ "Name": "portName", ... }] }
//
// Format is detected by the first non-whitespace character: '{' → JSON, else XML.
// =============================================================================

public static class DyfParser
{
    public static NodeRecord? Parse(string dyfContent, string packageName, string packageDescription, string[] packageKeywords)
    {
        var trimmed = dyfContent.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{'
            ? ParseJson(trimmed,   packageName, packageDescription, packageKeywords)
            : ParseXml(dyfContent, packageName, packageDescription, packageKeywords);
    }

    // ── JSON format (Dynamo 2.x) ───────────────────────────────────────────────

    private static NodeRecord? ParseJson(string json, string packageName, string packageDescription, string[] packageKeywords)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = GetString(root, "Name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var description = GetString(root, "Description");
            var category    = GetString(root, "Category");

            var inputs  = GetPortNames(root, "Inputs");
            var outputs = GetPortNames(root, "Outputs");

            return new NodeRecord
            {
                Name               = name.Trim(),
                PackageName        = packageName,
                PackageDescription = packageDescription,
                Description        = description?.Trim(),
                Category           = category?.Trim(),
                Keywords           = packageKeywords,
                InputPorts         = inputs,
                OutputPorts        = outputs,
                NodeType           = "DYF"
            };
        }
        catch { return null; }
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string[] GetPortNames(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object)
            .Select(p => GetString(p, "Name") ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();
    }

    // ── XML format (Dynamo 1.x) ────────────────────────────────────────────────

    private static NodeRecord? ParseXml(string dyfXml, string packageName, string packageDescription, string[] packageKeywords)
    {
        XDocument doc;
        try { doc = XDocument.Parse(dyfXml); }
        catch { return null; }

        var workspace = doc.Root;
        if (workspace == null) return null;

        var name = (string?)workspace.Attribute("Name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var description = (string?)workspace.Attribute("Description");
        var category    = (string?)workspace.Attribute("Category");

        // Input ports: Symbol elements (not Output)
        var inputs = workspace
            .Descendants()
            .Where(el =>
            {
                var type = (string?)el.Attribute("type") ?? el.Name.LocalName;
                return type.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                    && !type.Contains("Output", StringComparison.OrdinalIgnoreCase);
            })
            .Select(el => el.Element("Symbol"))
            .Where(sym => sym != null)
            .Select(sym => ((string?)sym!.Attribute("value") ?? "").Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();

        // Output ports
        var outputs = workspace
            .Descendants()
            .Where(el =>
            {
                var type = (string?)el.Attribute("type") ?? el.Name.LocalName;
                return type.Contains("Output", StringComparison.OrdinalIgnoreCase)
                    && type.Contains("CustomNode", StringComparison.OrdinalIgnoreCase);
            })
            .Select(el => el.Element("Symbol"))
            .Where(sym => sym != null)
            .Select(sym => ((string?)sym!.Attribute("value") ?? "").Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();

        return new NodeRecord
        {
            Name               = name.Trim(),
            PackageName        = packageName,
            PackageDescription = packageDescription,
            Description        = description?.Trim(),
            Category           = category?.Trim(),
            Keywords           = packageKeywords,
            InputPorts         = inputs,
            OutputPorts        = outputs,
            NodeType           = "DYF"
        };
    }
}
