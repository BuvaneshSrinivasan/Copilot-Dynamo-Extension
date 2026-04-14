using System.Xml.Linq;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Extractors;

// =============================================================================
// DyfParser — Extracts node metadata from Dynamo custom node (.dyf) files
// =============================================================================
// DYF files are XML workspaces. The root <Workspace> element carries the
// node's Name, Description, and Category attributes. Input/output ports are
// represented as <Symbol> nodes (inputs) and <Output> nodes within <Elements>.
//
// Example structure:
//   <Workspace Name="Springs.Dictionary.ByKeysValues"
//              Description="Dictionaries are an incredibly powerful..."
//              Category="Springs.Core.List.Create">
//     <Elements>
//       <Dynamo.Graph.Nodes.CustomNodes.Symbol nickname="Input">
//         <Symbol value="keys:var[]" />
//       </Dynamo.Graph.Nodes.CustomNodes.Symbol>
//       <Dynamo.Graph.Nodes.CustomNodes.Output nickname="Output">
//         <Symbol value="result" />
//       </Dynamo.Graph.Nodes.CustomNodes.Output>
//     </Elements>
//   </Workspace>
// =============================================================================

public static class DyfParser
{
    public static NodeRecord? Parse(string dyfXml, string packageName, string packageDescription, string[] packageKeywords)
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

        // ── INPUT PORTS ───────────────────────────────────────────────────────
        // Symbol elements whose parent has type containing "Symbol" (not Output)
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

        // ── OUTPUT PORTS ──────────────────────────────────────────────────────
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
