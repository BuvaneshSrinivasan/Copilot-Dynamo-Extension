using System.Xml.Linq;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Extractors;

// =============================================================================
// XmlDocParser — Extracts ZeroTouch node metadata from C# XML doc files
// =============================================================================
// XML doc files (e.g. DynaFly.xml) are produced by the C# compiler when
// <GenerateDocumentationFile> is enabled. They contain one <member> per
// public method/class with <summary>, <param>, and <returns> tags.
//
// A ZeroTouch node corresponds to a public static method. Its XML member name
// follows the format: M:Namespace.ClassName.MethodName(ParamType,...)
//
// We extract:
//   - Node name: ClassName.MethodName  (Dynamo's display convention)
//   - Category:  Namespace.ClassName
//   - Description: <summary> text
//   - Inputs: <param name="..."> tags
//   - Outputs: <returns> text (one implied output)
// =============================================================================

public static class XmlDocParser
{
    public static IReadOnlyList<NodeRecord> Parse(
        string xmlContent,
        string packageName,
        string packageDescription,
        string[] packageKeywords)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xmlContent); }
        catch { return []; }

        var members = doc.Descendants("member");
        var nodes = new List<NodeRecord>();

        foreach (var member in members)
        {
            var name = (string?)member.Attribute("name") ?? "";

            // Only process methods (M:) — skip properties (P:), types (T:), etc.
            if (!name.StartsWith("M:", StringComparison.Ordinal)) continue;

            var parsed = ParseMemberName(name);
            if (parsed == null) continue;

            var summary  = CleanDocText(member.Element("summary")?.Value);
            var returns  = CleanDocText(member.Element("returns")?.Value);

            var inputPorts = member.Elements("param")
                .Select(p => (string?)p.Attribute("name") ?? "")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            var outputPorts = returns != null
                ? new[] { returns }
                : Array.Empty<string>();

            nodes.Add(new NodeRecord
            {
                Name               = parsed.Value.NodeName,
                PackageName        = packageName,
                PackageDescription = packageDescription,
                Description        = summary,
                Category           = parsed.Value.Category,
                Keywords           = packageKeywords,
                InputPorts         = inputPorts,
                OutputPorts        = outputPorts,
                NodeType           = "ZeroTouch"
            });
        }

        return nodes;
    }

    // Parses "M:Autodesk.DesignScript.Geometry.Point.ByCoordinates(double,double)"
    // into NodeName="Point.ByCoordinates" and Category="Autodesk.DesignScript.Geometry"
    private static (string NodeName, string Category)? ParseMemberName(string memberName)
    {
        // Strip the "M:" prefix and the parameter list "(..)"
        var withoutPrefix = memberName[2..];
        var parenIdx = withoutPrefix.IndexOf('(');
        var fullName = parenIdx >= 0
            ? withoutPrefix[..parenIdx]
            : withoutPrefix;

        var lastDot = fullName.LastIndexOf('.');
        if (lastDot < 0) return null;

        var methodName = fullName[(lastDot + 1)..];
        var typePath   = fullName[..lastDot];

        // Skip constructors and compiler-generated methods
        if (methodName is "#ctor" or "Finalize" or "GetHashCode" or "Equals" or "ToString")
            return null;

        var secondLastDot = typePath.LastIndexOf('.');
        var className  = secondLastDot >= 0 ? typePath[(secondLastDot + 1)..] : typePath;
        var namespacePart = secondLastDot >= 0 ? typePath[..secondLastDot] : "";

        var nodeName = $"{className}.{methodName}";
        var category = string.IsNullOrEmpty(namespacePart) ? typePath : $"{namespacePart}.{className}";

        return (nodeName, category);
    }

    private static string? CleanDocText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Collapse whitespace sequences (XML doc text often has newlines + indentation)
        return System.Text.RegularExpressions.Regex
            .Replace(text.Trim(), @"\s+", " ");
    }
}
