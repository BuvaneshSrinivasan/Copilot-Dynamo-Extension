using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Extractors;

// =============================================================================
// PackageExtractor — Reads a Dynamo package and yields NodeRecords
// =============================================================================
// Extraction strategy:
//   1. pkg.json          → package name, description, keywords (always present)
//   2. dyf/*.dyf         → custom node name/desc/category/ports (unchanged)
//   3. bin/*.dll         → ZeroTouch nodes via MetadataLoadContext (primary)
//                          NodeModel nodes via [NodeName] attribute
//   4. bin/*.xml doc     → description enrichment for ZeroTouch nodes only
//
// The node_libraries filter from pkg.json is applied to DLLs (critical — mirrors
// Dynamo's Package.IsNodeLibrary logic to avoid indexing bundled third-party DLLs).
// =============================================================================

public static class PackageExtractor
{
    // .NET runtime DLLs — needed by MetadataLoadContext to resolve core types.
    // Cached once per process run.
    private static readonly string[] s_runtimeDlls =
        Directory.Exists(RuntimeEnvironment.GetRuntimeDirectory())
            ? Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            : [];

    // Dynamo SDK DLLs — needed to resolve Dynamo-defined attributes such as
    // [IsVisibleInDynamoLibrary], [NodeName], [NodeCategory], etc.
    // Without these, attribute type resolution throws and we default to
    // IsHidden=false, causing hidden nodes to appear in the index.
    // Scanned once from the newest installed Revit's DynamoForRevit folder.
    private static readonly string[] s_dynamoDlls = FindDynamoDlls();

    // DLL simple names that belong to the DynamoForRevit installation.
    // Built from s_dynamoDlls so it's always accurate for the installed version —
    // no hardcoded names. Packages that bundle these DLLs for compatibility are
    // skipped: the bundled copy is often older than what's installed and produces
    // stale ghost nodes.
    private static readonly HashSet<string> s_coreAssemblyBlocklist =
        new(s_dynamoDlls.Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n != null)
                        .Cast<string>(),
            StringComparer.OrdinalIgnoreCase);

    private static string[] FindDynamoDlls()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // Scan newest-first so we pick up the most recent Dynamo attribute definitions
        for (int year = 2030; year >= 2019; year--)
        {
            var dir = Path.Combine(programFiles, "Autodesk", $"Revit {year}", "AddIns", "DynamoForRevit");
            if (Directory.Exists(dir))
            {
                var dlls = Directory.GetFiles(dir, "*.dll");
                if (dlls.Length > 0)
                {
                    Console.WriteLine($"  Using Dynamo DLLs from: {dir}");
                    return dlls;
                }
            }
        }
        Console.WriteLine("  WARNING: No DynamoForRevit installation found — [IsVisibleInDynamoLibrary] filtering disabled.");
        return [];
    }

    private static readonly HashSet<string> s_objectMethodNames = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "GetType",
        "Finalize", "MemberwiseClone", "ReferenceEquals"
    };

    // ── Public entry points ────────────────────────────────────────────────────

    public static void InspectDll(string dllPath)
    {
        var binDir  = Path.GetDirectoryName(dllPath)!;
        var allDlls = Directory.GetFiles(binDir, "*.dll");
        var paths   = allDlls.Concat(s_runtimeDlls).Concat(s_dynamoDlls);

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var asm = mlc.LoadFromAssemblyPath(dllPath);

        Console.WriteLine("=== Referenced Assemblies ===");
        try
        {
            foreach (var r in asm.GetReferencedAssemblies().OrderBy(a => a.Name))
                Console.WriteLine($"  {r.Name}");
        }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }

        Console.WriteLine("\n=== Analysis class methods (return + param types) ===");
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException rtle)
            { types = rtle.Types.Where(t => t != null).Cast<Type>().ToArray(); }

        foreach (var t in types.Where(t => t.Name == "Analysis"))
        {
            MethodInfo[] methods;
            try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
            catch { continue; }

            foreach (var m in methods)
            {
                try
                {
                    var retType = m.ReturnType;
                    var retNs   = retType.Namespace ?? "";
                    Console.Write($"  {m.Name}  →  {retNs}.{retType.Name}");
                }
                catch { Console.Write($"  {m.Name}  →  <unresolvable>"); }

                try
                {
                    foreach (var p in m.GetParameters())
                        try { Console.Write($"  |  {p.ParameterType.Namespace}.{p.ParameterType.Name}"); }
                        catch { Console.Write("  |  <unresolvable>"); }
                }
                catch { Console.Write("  |  params:<error>"); }

                Console.WriteLine();
            }
        }
    }

    public static IReadOnlyList<NodeRecord> ExtractFromZip(string zipPath)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"DynIdx_{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true);
            var pkgRoot = FindPackageRoot(tmpDir);
            return pkgRoot != null ? ExtractFromDirectory(pkgRoot) : [];
        }
        catch { return []; }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

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

            // ── ZeroTouch + NodeModel via reflection ──────────────────────────
            var binDir = Path.Combine(packageDir, "bin");
            if (Directory.Exists(binDir))
                nodes.AddRange(ExtractReflectionNodes(
                    binDir, pkg.NodeLibraries, pkg.Name, pkg.Description ?? "", pkg.Keywords));

            return nodes;
        }
        catch { return []; }
    }

    // ── Reflection-based extraction ────────────────────────────────────────────

    private static IReadOnlyList<NodeRecord> ExtractReflectionNodes(
        string binDir, string[]? nodeLibraries,
        string packageName, string packageDesc, string[] keywords)
    {
        var nodes      = new List<NodeRecord>();
        var allBinDlls = Directory.GetFiles(binDir, "*.dll");
        if (allBinDlls.Length == 0) return nodes;

        // One MetadataLoadContext per package — all DLLs share the resolver so
        // cross-DLL type references within the package resolve correctly.
        MetadataLoadContext mlc;
        try
        {
            // Priority: package bin/ > .NET runtime > Dynamo SDK
            var resolver = new PathAssemblyResolver(allBinDlls.Concat(s_runtimeDlls).Concat(s_dynamoDlls));
            mlc = new MetadataLoadContext(resolver);
        }
        catch { return nodes; }

        using (mlc)
        {
            foreach (var dllPath in allBinDlls)
            {
                var baseName = Path.GetFileNameWithoutExtension(dllPath);
                if (!IsNodeLibraryDll(nodeLibraries, baseName)) continue;

                // Build XML doc description lookup (ClassName.MethodName → summary)
                var xmlDesc = BuildXmlDescLookup(Path.ChangeExtension(dllPath, ".xml"));

                Assembly asm;
                try { asm = mlc.LoadFromAssemblyPath(dllPath); }
                catch { continue; }

                // GetTypes() can throw ReflectionTypeLoadException when some types
                // cannot be loaded due to missing dependencies — use partial results.
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle)
                    { types = rtle.Types.Where(t => t != null).Cast<Type>().ToArray(); }

                foreach (var type in types)
                {
                    if (!type.IsPublic && !type.IsNestedPublic) continue;

                    // Skip compiler-generated types (<>c, <Method>d__0, etc.)
                    if (type.Name.Length > 0 && type.Name[0] == '<') continue;

                    IList<CustomAttributeData> typeAttrs;
                    try { typeAttrs = type.GetCustomAttributesData(); }
                    catch { continue; }

                    if (IsHiddenFromDynamo(typeAttrs)) continue;

                    // ── NodeModel: has [NodeName("...")] attribute ─────────────
                    if (HasAttributeNamed(typeAttrs, "NodeNameAttribute"))
                    {
                        if (type.IsAbstract) continue;  // abstract NodeModel base class

                        var nodeName = GetAttributeFirstStringArg(typeAttrs, "NodeNameAttribute");
                        if (string.IsNullOrWhiteSpace(nodeName)) continue;

                        nodes.Add(new NodeRecord
                        {
                            Name               = nodeName,
                            PackageName        = packageName,
                            PackageDescription = packageDesc,
                            Description        = GetAttributeFirstStringArg(typeAttrs, "TooltipAttribute"),
                            Category           = GetAttributeFirstStringArg(typeAttrs, "NodeCategoryAttribute"),
                            Keywords           = keywords,
                            NodeType           = "NodeModel"
                        });
                        continue;
                    }

                    // ── ZeroTouch: scan public methods ────────────────────────────
                    // static classes compile to IsAbstract=true + IsSealed=true — allow them.
                    // Skip only truly abstract types (abstract base classes, interfaces).
                    if (type.IsAbstract && !type.IsSealed) continue;

                    // DeclaredOnly: avoids base-class traversal into cross-package or
                    // Dynamo assemblies not in the resolver — prevents stack overflows
                    // from MLC resolving deeply nested generic inheritance chains.
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (var method in methods)
                    {
                        if (!IsZeroTouchMethod(method)) continue;

                        IList<CustomAttributeData> methodAttrs;
                        try { methodAttrs = method.GetCustomAttributesData(); }
                        catch { continue; }

                        if (IsHiddenFromDynamo(methodAttrs)) continue;

                        var nodeName = $"{type.Name}.{method.Name}";
                        xmlDesc.TryGetValue(nodeName, out var desc);

                        nodes.Add(new NodeRecord
                        {
                            Name               = nodeName,
                            PackageName        = packageName,
                            PackageDescription = packageDesc,
                            Description        = desc,
                            Category           = $"{type.Namespace}.{type.Name}",
                            Keywords           = keywords,
                            NodeType           = "ZeroTouch"
                        });
                    }
                }
            }
        }

        return nodes;
    }

    // ── Attribute helpers (use GetCustomAttributesData, NOT GetCustomAttributes) ─
    // GetCustomAttributes instantiates attribute types which requires loading
    // Dynamo assemblies not present in the indexer context.

    private static bool IsZeroTouchMethod(MethodInfo method)
    {
        if (s_objectMethodNames.Contains(method.Name)) return false;
        if (method.Name.StartsWith("get_",    StringComparison.Ordinal) ||
            method.Name.StartsWith("set_",    StringComparison.Ordinal) ||
            method.Name.StartsWith("add_",    StringComparison.Ordinal) ||
            method.Name.StartsWith("remove_", StringComparison.Ordinal))
            return false;
        return true;
    }

    // All three helpers iterate with per-attribute try-catch because accessing
    // a.AttributeType triggers MLC assembly resolution. Dynamo attribute assemblies
    // (DynamoCore, DynamoServices, ProtoGeometry) are not in the resolver — accessing
    // their attribute types throws FileNotFoundException. We swallow per-attribute
    // and continue so one unresolvable attribute never kills the whole package.

    private static bool HasAttributeNamed(IList<CustomAttributeData> attrs, string typeName)
    {
        foreach (var a in attrs)
        {
            try { if (a.AttributeType.Name == typeName) return true; }
            catch { }
        }
        return false;
    }

    private static bool IsHiddenFromDynamo(IList<CustomAttributeData> attrs)
    {
        foreach (var a in attrs)
        {
            try
            {
                if (a.AttributeType.Name != "IsVisibleInDynamoLibraryAttribute") continue;
                var arg = a.ConstructorArguments.FirstOrDefault();
                return arg.Value is false;
            }
            catch { }
        }
        return false;
    }

    private static string? GetAttributeFirstStringArg(IList<CustomAttributeData> attrs, string typeName)
    {
        foreach (var a in attrs)
        {
            try
            {
                if (a.AttributeType.Name != typeName) continue;
                var arg = a.ConstructorArguments.FirstOrDefault();
                return arg.Value as string;
            }
            catch { }
        }
        return null;
    }

    // ── XML doc description enrichment ────────────────────────────────────────
    // Builds a lookup of "ClassName.MethodName" → summary text.
    // Used after reflection discovers nodes — XML docs are not the source of truth.

    private static Dictionary<string, string> BuildXmlDescLookup(string xmlFilePath)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(xmlFilePath)) return lookup;
        try
        {
            var doc = XDocument.Load(xmlFilePath);
            foreach (var member in doc.Descendants("member"))
            {
                var name = (string?)member.Attribute("name") ?? "";
                if (!name.StartsWith("M:", StringComparison.Ordinal)) continue;

                var withoutPrefix = name[2..];
                var parenIdx      = withoutPrefix.IndexOf('(');
                var fullName      = parenIdx >= 0 ? withoutPrefix[..parenIdx] : withoutPrefix;
                var lastDot       = fullName.LastIndexOf('.');
                if (lastDot < 0) continue;

                var methodName = fullName[(lastDot + 1)..];
                var typePath   = fullName[..lastDot];
                var secondLast = typePath.LastIndexOf('.');
                var className  = secondLast >= 0 ? typePath[(secondLast + 1)..] : typePath;

                var key     = $"{className}.{methodName}";
                var summary = CleanDocText(member.Element("summary")?.Value);
                if (summary != null && !lookup.ContainsKey(key))
                    lookup[key] = summary;
            }
        }
        catch { }
        return lookup;
    }

    // ── Package helpers ────────────────────────────────────────────────────────

    // Some packages zip their content inside a root folder; others have pkg.json
    // at the zip root. Walk one level deep to find pkg.json.
    private static string? FindPackageRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "pkg.json"))) return extractDir;
        foreach (var sub in Directory.GetDirectories(extractDir))
            if (File.Exists(Path.Combine(sub, "pkg.json"))) return sub;
        return null;
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

            // node_libraries absent → null (legacy; index all DLLs, matching Dynamo behaviour)
            // node_libraries present but empty → [] (package has no node libraries)
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

    private static string? CleanDocText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// Returns true when a DLL should be indexed for this package.
    /// Mirrors Dynamo's Package.IsNodeLibrary logic (Package.cs:459):
    ///   - nodeLibraries null  → legacy package, index all DLLs
    ///   - nodeLibraries empty → package has no node libraries, skip all
    ///   - otherwise           → only index DLLs listed there
    /// Core Dynamo/Revit assemblies are always skipped even if listed in
    /// node_libraries — some packages bundle them for compatibility but indexing
    /// them produces stale ghost nodes from the older bundled version.
    /// </summary>
    private static bool IsNodeLibraryDll(string[]? nodeLibraries, string dllBaseName)
    {
        if (s_coreAssemblyBlocklist.Contains(dllBaseName)) return false;

        if (nodeLibraries == null) return true;
        foreach (var entry in nodeLibraries)
        {
            // entry format: "AssemblySimpleName, Version=x.x, Culture=..., PublicKeyToken=..."
            var simpleName = entry.Contains(',') ? entry[..entry.IndexOf(',')].Trim() : entry.Trim();
            if (simpleName.Equals(dllBaseName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record PkgInfo(string? Name, string? Description, string[] Keywords, string[]? NodeLibraries);
}
