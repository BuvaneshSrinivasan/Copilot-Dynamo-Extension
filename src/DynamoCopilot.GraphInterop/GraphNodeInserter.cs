using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace DynamoCopilot.GraphInterop
{
    /// <summary>
    /// Inserts a Dynamo node (ZeroTouch DLL node or DYF custom node) onto the canvas
    /// using Dynamo's command infrastructure via reflection.
    /// </summary>
    public static class GraphNodeInserter
    {
        /// <summary>
        /// Inserts a node onto the current canvas.
        /// For DYF nodes the DYF file is located in the installed package folder,
        /// loaded into Dynamo's CustomNodeManager (if not already present), then placed.
        /// For ZeroTouch / XmlDoc nodes the fully-qualified type name is used directly.
        /// </summary>
        /// <param name="dynamoModel">DynamoModel instance (via reflection).</param>
        /// <param name="nodeName">Node name as stored in the index (e.g. "Rhythm.Views.SheetByName").</param>
        /// <param name="packageName">Package the node belongs to.</param>
        /// <param name="nodeType">"DYF", "ZeroTouch", or "XmlDoc".</param>
        /// <param name="packageFolderPath">
        /// Full path to the installed package folder (e.g. "…\packages\Rhythm").
        /// Required for DYF nodes; may be null for ZeroTouch nodes.
        /// </param>
        /// <returns>True if insertion succeeded.</returns>
        public static bool InsertNode(
            object  dynamoModel,
            string  nodeName,
            string  packageName,
            string  nodeType,
            string? packageFolderPath,
            double  canvasX = 0,
            double  canvasY = 0)
        {
            if (dynamoModel == null || string.IsNullOrWhiteSpace(nodeName))
                return false;

            bool isZeroTouch = nodeType != null &&
                (nodeType.Equals("ZeroTouch", StringComparison.OrdinalIgnoreCase) ||
                 nodeType.Equals("XmlDoc",    StringComparison.OrdinalIgnoreCase));

            if (isZeroTouch)
                return InsertZeroTouchNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY);

            // DYF, JBNode, or any unrecognised type — try DYF first, fall back to ZeroTouch
            return InsertDyfNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY)
                || InsertZeroTouchNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY);
        }

        // ── ZeroTouch / XmlDoc ────────────────────────────────────────────────

        private static bool InsertZeroTouchNode(
            object dynamoModel, string nodeName, string? packageFolderPath, double x, double y)
        {
            try
            {
                var workspace = GetCurrentWorkspace(dynamoModel);
                if (workspace == null) return false;

                // Resolve the exact creation name from the package's loaded assemblies
                // so we pick the right node when multiple packages share the same node name.
                var creationName = ResolveCreationName(nodeName, packageFolderPath);

                var node = ExecuteCreateNode(dynamoModel, workspace, creationName, x, y);
                return node != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Finds the fully-qualified CLR creation name for a ZeroTouch node by scanning
        /// assemblies that are already loaded from the package's bin folder.
        /// Falls back to the bare nodeName if the package assembly isn't loaded yet.
        /// </summary>
        private static string ResolveCreationName(string nodeName, string? packageFolderPath)
        {
            if (string.IsNullOrEmpty(packageFolderPath)) return nodeName;

            var binDir = Path.Combine(packageFolderPath, "bin");
            if (!Directory.Exists(binDir)) return nodeName;

            // Build a set of DLL paths inside the package's bin folder (case-insensitive on Windows)
            var packageAsmPaths = new HashSet<string>(
                Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories),
                StringComparer.OrdinalIgnoreCase);

            // nodeName format: "TypeName.MethodName" or just "TypeName"
            var parts      = nodeName.Split('.');
            var methodPart = parts[parts.Length - 1];
            var typePart   = parts.Length >= 2 ? parts[parts.Length - 2] : methodPart;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!packageAsmPaths.Contains(asm.Location)) continue;

                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (!type.Name.Equals(typePart, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Check for a matching public static/instance method
                        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name.Equals(methodPart, StringComparison.OrdinalIgnoreCase));

                        if (method != null)
                            return type.FullName + "." + method.Name;

                        // Constructor node: TypeName.TypeName
                        if (type.Name.Equals(methodPart, StringComparison.OrdinalIgnoreCase))
                            return type.FullName;
                    }
                }
                catch { /* skip assemblies that can't be inspected */ }
            }

            return nodeName; // fallback
        }

        // ── DYF custom node ───────────────────────────────────────────────────

        private static bool InsertDyfNode(
            object  dynamoModel,
            string  nodeName,
            string? packageFolderPath,
            double  x,
            double  y)
        {
            try
            {
                Guid guid;

                var dyfPath = FindDyfFile(nodeName, packageFolderPath);
                if (dyfPath != null)
                {
                    guid = ParseDyfGuid(dyfPath);
                    if (guid == Guid.Empty) return false;
                    if (!EnsureCustomNodeLoaded(dynamoModel, dyfPath, guid))
                        return false;
                }
                else
                {
                    // Package folder path unavailable or DYF not found by file name —
                    // search Dynamo's CustomNodeManager for an already-loaded definition.
                    guid = FindCustomNodeGuidByName(dynamoModel, nodeName);
                    if (guid == Guid.Empty) return false;
                }

                var workspace = GetCurrentWorkspace(dynamoModel);
                if (workspace == null) return false;

                var node = ExecuteCreateNode(dynamoModel, workspace, guid.ToString(), x, y);
                return node != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Searches DynamoModel.CustomNodeManager for a loaded custom-node definition
        /// whose name matches <paramref name="nodeName"/> (simple or fully-qualified).
        /// Returns <see cref="Guid.Empty"/> when not found.
        ///
        /// Verified reflection chain (DLL inspection):
        ///   CustomNodeManager.LoadedDefinitions → IEnumerable&lt;CustomNodeDefinition&gt;
        ///     CustomNodeDefinition.FunctionName  (display name)
        ///     CustomNodeDefinition.FunctionId    (Guid — used in CreateNodeCommand)
        ///   CustomNodeManager.LoadedWorkspaces  → IEnumerable&lt;CustomNodeWorkspaceModel&gt;
        ///     CustomNodeWorkspaceModel.Name       (display name)
        ///     CustomNodeWorkspaceModel.CustomNodeId (Guid)
        /// </summary>
        private static Guid FindCustomNodeGuidByName(object dynamoModel, string nodeName)
        {
            try
            {
                var cnm = dynamoModel.GetType()
                    .GetProperty("CustomNodeManager", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);
                if (cnm == null) return Guid.Empty;

                var simpleName = nodeName.Contains('.')
                    ? nodeName.Substring(nodeName.LastIndexOf('.') + 1)
                    : nodeName;

                bool Matches(string? s) =>
                    s != null &&
                    (s.Equals(simpleName, StringComparison.OrdinalIgnoreCase) ||
                     s.Equals(nodeName,   StringComparison.OrdinalIgnoreCase));

                // ── 1. LoadedDefinitions (CustomNodeDefinition: FunctionName, FunctionId) ──
                if (cnm.GetType()
                        .GetProperty("LoadedDefinitions", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(cnm) is System.Collections.IEnumerable defs)
                {
                    foreach (var def in defs)
                    {
                        if (def == null) continue;
                        var dt = def.GetType();
                        if (!Matches(dt.GetProperty("FunctionName", BindingFlags.Public | BindingFlags.Instance)
                                       ?.GetValue(def) as string)) continue;

                        if (dt.GetProperty("FunctionId", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(def) is Guid g && g != Guid.Empty) return g;
                    }
                }

                // ── 2. LoadedWorkspaces (CustomNodeWorkspaceModel: Name, CustomNodeId) ──
                if (cnm.GetType()
                        .GetProperty("LoadedWorkspaces", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(cnm) is System.Collections.IEnumerable workspaces)
                {
                    foreach (var ws in workspaces)
                    {
                        if (ws == null) continue;
                        var wt = ws.GetType();
                        if (!Matches(wt.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                                       ?.GetValue(ws) as string)) continue;

                        if (wt.GetProperty("CustomNodeId", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(ws) is Guid g && g != Guid.Empty) return g;
                    }
                }

                return Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        private static string? FindDyfFile(string nodeName, string? packageFolderPath)
        {
            if (string.IsNullOrEmpty(packageFolderPath)) return null;

            var dyfDir = Path.Combine(packageFolderPath, "dyf");
            if (!Directory.Exists(dyfDir)) return null;

            // Strip any namespace prefix: "Rhythm.Views.SheetByName" → "SheetByName"
            var dotIdx = nodeName.LastIndexOf('.');
            var simpleName = dotIdx >= 0
                ? nodeName.Substring(dotIdx + 1)
                : nodeName;

            // Try exact match first, then case-insensitive search
            var exact = Path.Combine(dyfDir, simpleName + ".dyf");
            if (File.Exists(exact)) return exact;

            foreach (var f in Directory.EnumerateFiles(dyfDir, "*.dyf"))
            {
                if (Path.GetFileNameWithoutExtension(f)
                    .Equals(simpleName, StringComparison.OrdinalIgnoreCase))
                    return f;
            }

            // Broader search: check if any DYF has a matching Name attribute inside
            foreach (var f in Directory.EnumerateFiles(dyfDir, "*.dyf"))
            {
                try
                {
                    var xDoc = XDocument.Load(f);
                    var wsName = xDoc.Root?.Attribute("Name")?.Value ?? string.Empty;
                    if (wsName.Equals(simpleName, StringComparison.OrdinalIgnoreCase) ||
                        wsName.Equals(nodeName,   StringComparison.OrdinalIgnoreCase))
                        return f;
                }
                catch { }
            }

            return null;
        }

        private static Guid ParseDyfGuid(string dyfPath)
        {
            try
            {
                var xDoc = XDocument.Load(dyfPath);
                // DYF workspace element carries ID="…" or FunctionId="…"
                var root = xDoc.Root;
                var idStr = root?.Attribute("ID")?.Value
                         ?? root?.Attribute("FunctionId")?.Value
                         ?? root?.Attribute("id")?.Value;

                return Guid.TryParse(idStr, out var g) ? g : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        private static bool EnsureCustomNodeLoaded(object dynamoModel, string dyfPath, Guid guid)
        {
            try
            {
                var cnm = dynamoModel.GetType()
                    .GetProperty("CustomNodeManager", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);
                if (cnm == null) return false;

                // Contains(Guid) — verified public method on CustomNodeManager
                var containsGuid = cnm.GetType().GetMethod("Contains",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Guid) }, null);
                if (containsGuid?.Invoke(cnm, new object[] { guid }) is true)
                    return true;

                // AddUninitializedCustomNode(string file, bool isTestMode, out CustomNodeInfo info)
                // — verified public method; loads the DYF and registers it.
                var addMethod = cnm.GetType().GetMethod(
                    "AddUninitializedCustomNode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (addMethod != null)
                {
                    var args = new object?[] { dyfPath, false, null };
                    try
                    {
                        var ok = addMethod.Invoke(cnm, args) as bool? ?? false;
                        if (ok) return true;
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        // ── Shared Dynamo command helpers ─────────────────────────────────────

        private static object? GetCurrentWorkspace(object dynamoModel)
        {
            return dynamoModel.GetType()
                .GetProperty("CurrentWorkspace", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(dynamoModel);
        }

        private static object? ExecuteCreateNode(
            object dynamoModel,
            object workspace,
            string nodeTypeName,
            double x = 0,
            double y = 0)
        {
            try
            {
                var cmdType = FindType("Dynamo.Models.DynamoModel+CreateNodeCommand");
                if (cmdType == null) return null;

                // CreateNodeCommand(string nodeId, string nodeName,
                //                   double x, double y,
                //                   bool defaultPosition, bool transformCoordinates)
                var ctor = cmdType.GetConstructor(new[]
                {
                    typeof(string), typeof(string),
                    typeof(double), typeof(double),
                    typeof(bool),   typeof(bool)
                });
                if (ctor == null) return null;

                var newGuid = Guid.NewGuid().ToString();
                // defaultPosition=false + explicit coordinates places the node at canvas center.
                var cmd     = ctor.Invoke(new object[]
                    { newGuid, nodeTypeName, x, y, false, false });

                var executeMethod = dynamoModel.GetType()
                    .GetMethod("ExecuteCommand", BindingFlags.Public | BindingFlags.Instance);
                executeMethod?.Invoke(dynamoModel, new[] { cmd });

                // Locate the created node by its GUID
                var nodesEnumerable = workspace.GetType()
                    .GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(workspace) as System.Collections.IEnumerable;

                if (nodesEnumerable == null) return null;

                foreach (var n in nodesEnumerable)
                {
                    if (n == null) continue;
                    var guidVal = n.GetType()
                        .GetProperty("GUID",
                            BindingFlags.Public | BindingFlags.Instance |
                            BindingFlags.FlattenHierarchy)
                        ?.GetValue(n);
                    if (guidVal?.ToString() == newGuid) return n;
                }

                return null;
            }
            catch { return null; }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
