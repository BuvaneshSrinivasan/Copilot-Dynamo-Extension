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
        /// <param name="log">Optional logger — receives diagnostic messages at each step.</param>
        /// <returns>True if insertion succeeded.</returns>
        public static bool InsertNode(
            object          dynamoModel,
            string          nodeName,
            string          packageName,
            string          nodeType,
            string?         packageFolderPath,
            double          canvasX = 0,
            double          canvasY = 0,
            Action<string>? log     = null)
        {
            log?.Invoke($"[NodeInserter] InsertNode START — node={nodeName}  package={packageName}  type={nodeType}  pkgFolder={packageFolderPath}");

            if (dynamoModel == null || string.IsNullOrWhiteSpace(nodeName))
            {
                log?.Invoke("[NodeInserter] ABORT — dynamoModel is null or nodeName is blank");
                return false;
            }

            bool isZeroTouch = nodeType != null &&
                (nodeType.Equals("ZeroTouch", StringComparison.OrdinalIgnoreCase) ||
                 nodeType.Equals("XmlDoc",    StringComparison.OrdinalIgnoreCase));

            log?.Invoke($"[NodeInserter] isZeroTouch={isZeroTouch}");

            if (isZeroTouch)
                return InsertZeroTouchNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY, log);

            // DYF, JBNode, or any unrecognised type — try DYF first, fall back to ZeroTouch
            log?.Invoke("[NodeInserter] Trying DYF path first, then ZeroTouch fallback");
            return InsertDyfNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY, log)
                || InsertZeroTouchNode(dynamoModel, nodeName, packageFolderPath, canvasX, canvasY, log);
        }

        // ── ZeroTouch / XmlDoc ────────────────────────────────────────────────

        private static bool InsertZeroTouchNode(
            object dynamoModel, string nodeName, string? packageFolderPath, double x, double y,
            Action<string>? log)
        {
            try
            {
                var workspace = GetCurrentWorkspace(dynamoModel);
                if (workspace == null)
                {
                    log?.Invoke("[NodeInserter] ZT FAIL — CurrentWorkspace is null");
                    return false;
                }

                // ResolveCreationName returns "ClassName.MethodName" (no namespace) which matches
                // FunctionDescriptor.QualifiedName — the key in LibraryServices' function group map.
                var creationName = ResolveCreationName(nodeName, packageFolderPath, log);
                log?.Invoke($"[NodeInserter] ResolveCreationName returned: '{creationName}'");

                // For overloaded nodes, QualifiedName alone fails FunctionGroup.GetFunctionDescriptor's
                // EndsWith check (Dynamo source: FunctionGroup.cs:71). Promote to the real MangledName
                // ("ClassName.Method@T1,T2") by querying LibraryServices directly.
                var mangledName = TryResolveMangledName(dynamoModel, creationName, log);
                log?.Invoke($"[NodeInserter] TryResolveMangledName returned: '{mangledName ?? "(null)"}'");
                if (mangledName != null) creationName = mangledName;

                log?.Invoke($"[NodeInserter] Calling ExecuteCreateNode with creationName='{creationName}'");
                var node = ExecuteCreateNode(dynamoModel, workspace, creationName, x, y, log);
                var ok = node != null;
                log?.Invoke($"[NodeInserter] ZT result: node found in workspace={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[NodeInserter] ZT EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Looks up the exact MangledName from DynamoModel.LibraryServices so that both
        // non-overloaded ("ClassName.Method") and overloaded ("ClassName.Method@T1,T2") nodes
        // resolve correctly inside GetNodeFromCommand → CreateNodeFromNameOrType.
        private static string? TryResolveMangledName(object dynamoModel, string qualifiedName, Action<string>? log)
        {
            try
            {
                // LibraryServices is internal on DynamoModel — include NonPublic flag.
                var libServices = dynamoModel.GetType()
                    .GetProperty("LibraryServices",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);
                if (libServices == null)
                {
                    log?.Invoke("[NodeInserter] TryResolveMangledName — LibraryServices not found on model (tried Public+NonPublic)");
                    return null;
                }

                var lsType = libServices.GetType();
                log?.Invoke($"[NodeInserter] TryResolveMangledName — LibraryServices type={lsType.FullName}");

                // Non-overloaded: GetFunctionDescriptor(string) finds by QualifiedName
                var getDesc = lsType.GetMethod("GetFunctionDescriptor",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                log?.Invoke($"[NodeInserter] GetFunctionDescriptor method found={getDesc != null}");

                var desc = getDesc?.Invoke(libServices, new object[] { qualifiedName });
                if (desc != null)
                {
                    var mn = desc.GetType()
                        .GetProperty("MangledName", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(desc) as string;
                    log?.Invoke($"[NodeInserter] GetFunctionDescriptor hit — MangledName='{mn}'");
                    return mn;
                }

                log?.Invoke($"[NodeInserter] GetFunctionDescriptor returned null for '{qualifiedName}' — trying GetAllFunctionDescriptors (overload case)");

                // Overloaded: GetAllFunctionDescriptors returns every overload; take the first.
                var getAll = lsType.GetMethod("GetAllFunctionDescriptors",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                log?.Invoke($"[NodeInserter] GetAllFunctionDescriptors method found={getAll != null}");

                if (getAll?.Invoke(libServices, new object[] { qualifiedName })
                    is System.Collections.IEnumerable all)
                {
                    foreach (var d in all)
                    {
                        if (d == null) continue;
                        var mn = d.GetType()
                            .GetProperty("MangledName", BindingFlags.Public | BindingFlags.Instance)
                            ?.GetValue(d) as string;
                        log?.Invoke($"[NodeInserter] GetAllFunctionDescriptors first hit — MangledName='{mn}'");
                        return mn;
                    }
                    log?.Invoke($"[NodeInserter] GetAllFunctionDescriptors returned empty for '{qualifiedName}'");
                }

                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[NodeInserter] TryResolveMangledName EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the creation name for a ZeroTouch node by scanning assemblies already
        /// loaded from the package's bin folder.
        /// Returns "ClassName.MethodName" (simple class name, no namespace) to match
        /// FunctionDescriptor.QualifiedName — the key used by LibraryServices.
        /// Falls back to the bare nodeName if the assembly isn't loaded yet.
        /// </summary>
        private static string ResolveCreationName(string nodeName, string? packageFolderPath, Action<string>? log)
        {
            if (string.IsNullOrEmpty(packageFolderPath))
            {
                log?.Invoke($"[NodeInserter] ResolveCreationName — no packageFolderPath, using nodeName as-is: '{nodeName}'");
                return nodeName;
            }

            var binDir = Path.Combine(packageFolderPath, "bin");
            if (!Directory.Exists(binDir))
            {
                log?.Invoke($"[NodeInserter] ResolveCreationName — bin dir not found: '{binDir}', using nodeName as-is");
                return nodeName;
            }

            var packageAsmPaths = new HashSet<string>(
                Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories),
                StringComparer.OrdinalIgnoreCase);
            log?.Invoke($"[NodeInserter] ResolveCreationName — {packageAsmPaths.Count} DLL(s) in bin: {string.Join(", ", packageAsmPaths.Select(Path.GetFileName))}");

            var parts      = nodeName.Split('.');
            var methodPart = parts[parts.Length - 1];
            var typePart   = parts.Length >= 2 ? parts[parts.Length - 2] : methodPart;
            log?.Invoke($"[NodeInserter] ResolveCreationName — looking for type='{typePart}' method='{methodPart}'");

            int scannedAsms = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!packageAsmPaths.Contains(asm.Location)) continue;
                    scannedAsms++;
                    log?.Invoke($"[NodeInserter] ResolveCreationName — scanning assembly: {asm.GetName().Name}  Location={asm.Location}");

                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (!type.Name.Equals(typePart, StringComparison.OrdinalIgnoreCase))
                            continue;

                        log?.Invoke($"[NodeInserter] ResolveCreationName — matched type: FullName={type.FullName}  Name={type.Name}");

                        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name.Equals(methodPart, StringComparison.OrdinalIgnoreCase));

                        if (method != null)
                        {
                            // Use type.Name (NOT type.FullName) — LibraryServices keys by
                            // FunctionDescriptor.QualifiedName = "ClassName.MethodName", no namespace.
                            var resolved = type.Name + "." + method.Name;
                            log?.Invoke($"[NodeInserter] ResolveCreationName — resolved to '{resolved}' (type.FullName was '{type.FullName}')");
                            return resolved;
                        }

                        // Constructor node: TypeName.TypeName
                        if (type.Name.Equals(methodPart, StringComparison.OrdinalIgnoreCase))
                        {
                            var resolved = type.Name;
                            log?.Invoke($"[NodeInserter] ResolveCreationName — constructor node, resolved to '{resolved}'");
                            return resolved;
                        }

                        log?.Invoke($"[NodeInserter] ResolveCreationName — type matched but method '{methodPart}' not found on it");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[NodeInserter] ResolveCreationName — skipping assembly ({ex.GetType().Name}: {ex.Message})");
                }
            }

            log?.Invoke($"[NodeInserter] ResolveCreationName — scanned {scannedAsms} package assembly/assemblies, no match found, falling back to nodeName='{nodeName}'");
            return nodeName;
        }

        // ── DYF custom node ───────────────────────────────────────────────────

        private static bool InsertDyfNode(
            object  dynamoModel,
            string  nodeName,
            string? packageFolderPath,
            double  x,
            double  y,
            Action<string>? log)
        {
            try
            {
                Guid guid;

                var dyfPath = FindDyfFile(nodeName, packageFolderPath, log);
                if (dyfPath != null)
                {
                    log?.Invoke($"[NodeInserter] DYF — found file: {dyfPath}");
                    guid = ParseDyfGuid(dyfPath);
                    log?.Invoke($"[NodeInserter] DYF — parsed GUID: {guid}");
                    if (guid == Guid.Empty)
                    {
                        log?.Invoke("[NodeInserter] DYF FAIL — GUID is empty");
                        return false;
                    }
                    // Best-effort: try to register the DYF via our reflection path.
                    // We do NOT abort on false — DynamoModel.GetNodeFromCommand accesses
                    // CustomNodeManager internally when processing CreateNodeCommand(guid),
                    // so installed-at-startup packages work even if we can't reach it.
                    var registered = EnsureCustomNodeLoaded(dynamoModel, dyfPath, guid, log);
                    log?.Invoke($"[NodeInserter] DYF — EnsureCustomNodeLoaded={registered}, proceeding to ExecuteCreateNode regardless");
                }
                else
                {
                    log?.Invoke("[NodeInserter] DYF — no .dyf file found by name, searching CustomNodeManager");
                    guid = FindCustomNodeGuidByName(dynamoModel, nodeName, log);
                    log?.Invoke($"[NodeInserter] DYF — FindCustomNodeGuidByName returned: {guid}");
                    if (guid == Guid.Empty)
                    {
                        log?.Invoke("[NodeInserter] DYF FAIL — custom node GUID not found in CustomNodeManager");
                        return false;
                    }
                }

                var workspace = GetCurrentWorkspace(dynamoModel);
                if (workspace == null)
                {
                    log?.Invoke("[NodeInserter] DYF FAIL — CurrentWorkspace is null");
                    return false;
                }

                log?.Invoke($"[NodeInserter] DYF — calling ExecuteCreateNode with guid='{guid}'");
                var node = ExecuteCreateNode(dynamoModel, workspace, guid.ToString(), x, y, log);
                var ok = node != null;
                log?.Invoke($"[NodeInserter] DYF result: node found in workspace={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[NodeInserter] DYF EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Guid FindCustomNodeGuidByName(object dynamoModel, string nodeName, Action<string>? log)
        {
            try
            {
                var cnm = dynamoModel.GetType()
                    .GetProperty("CustomNodeManager",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);
                if (cnm == null)
                {
                    log?.Invoke("[NodeInserter] FindCustomNodeGuidByName — CustomNodeManager not found (tried Public+NonPublic)");
                    return Guid.Empty;
                }

                var simpleName = nodeName.Contains('.')
                    ? nodeName.Substring(nodeName.LastIndexOf('.') + 1)
                    : nodeName;

                bool Matches(string? s) =>
                    s != null &&
                    (s.Equals(simpleName, StringComparison.OrdinalIgnoreCase) ||
                     s.Equals(nodeName,   StringComparison.OrdinalIgnoreCase));

                if (cnm.GetType()
                        .GetProperty("LoadedDefinitions", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(cnm) is System.Collections.IEnumerable defs)
                {
                    foreach (var def in defs)
                    {
                        if (def == null) continue;
                        var dt = def.GetType();
                        var fn = dt.GetProperty("FunctionName", BindingFlags.Public | BindingFlags.Instance)
                                   ?.GetValue(def) as string;
                        if (!Matches(fn)) continue;

                        if (dt.GetProperty("FunctionId", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(def) is Guid g && g != Guid.Empty)
                        {
                            log?.Invoke($"[NodeInserter] FindCustomNodeGuidByName — found in LoadedDefinitions: {g}");
                            return g;
                        }
                    }
                }

                if (cnm.GetType()
                        .GetProperty("LoadedWorkspaces", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(cnm) is System.Collections.IEnumerable workspaces)
                {
                    foreach (var ws in workspaces)
                    {
                        if (ws == null) continue;
                        var wt = ws.GetType();
                        var wsName = wt.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                                       ?.GetValue(ws) as string;
                        if (!Matches(wsName)) continue;

                        if (wt.GetProperty("CustomNodeId", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(ws) is Guid g && g != Guid.Empty)
                        {
                            log?.Invoke($"[NodeInserter] FindCustomNodeGuidByName — found in LoadedWorkspaces: {g}");
                            return g;
                        }
                    }
                }

                log?.Invoke($"[NodeInserter] FindCustomNodeGuidByName — not found for '{nodeName}' (simpleName='{simpleName}')");
                return Guid.Empty;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[NodeInserter] FindCustomNodeGuidByName EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return Guid.Empty;
            }
        }

        private static string? FindDyfFile(string nodeName, string? packageFolderPath, Action<string>? log)
        {
            if (string.IsNullOrEmpty(packageFolderPath)) return null;

            var dyfDir = Path.Combine(packageFolderPath, "dyf");
            if (!Directory.Exists(dyfDir))
            {
                log?.Invoke($"[NodeInserter] FindDyfFile — dyf dir not found: {dyfDir}");
                return null;
            }

            var dotIdx    = nodeName.LastIndexOf('.');
            var simpleName = dotIdx >= 0 ? nodeName.Substring(dotIdx + 1) : nodeName;

            var exact = Path.Combine(dyfDir, simpleName + ".dyf");
            if (File.Exists(exact)) return exact;

            foreach (var f in Directory.EnumerateFiles(dyfDir, "*.dyf"))
            {
                if (Path.GetFileNameWithoutExtension(f)
                    .Equals(simpleName, StringComparison.OrdinalIgnoreCase))
                    return f;
            }

            foreach (var f in Directory.EnumerateFiles(dyfDir, "*.dyf"))
            {
                try
                {
                    var xDoc   = XDocument.Load(f);
                    var wsName = xDoc.Root?.Attribute("Name")?.Value ?? string.Empty;
                    if (wsName.Equals(simpleName, StringComparison.OrdinalIgnoreCase) ||
                        wsName.Equals(nodeName,   StringComparison.OrdinalIgnoreCase))
                        return f;
                }
                catch { }
            }

            log?.Invoke($"[NodeInserter] FindDyfFile — no .dyf found for '{nodeName}' in {dyfDir}");
            return null;
        }

        private static Guid ParseDyfGuid(string dyfPath)
        {
            try
            {
                var xDoc  = XDocument.Load(dyfPath);
                var root  = xDoc.Root;
                var idStr = root?.Attribute("ID")?.Value
                         ?? root?.Attribute("FunctionId")?.Value
                         ?? root?.Attribute("id")?.Value;
                return Guid.TryParse(idStr, out var g) ? g : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        private static bool EnsureCustomNodeLoaded(object dynamoModel, string dyfPath, Guid guid, Action<string>? log)
        {
            try
            {
                // CustomNodeManager may be internal on DynamoModel (base of RevitDynamoModel).
                var cnm = dynamoModel.GetType()
                    .GetProperty("CustomNodeManager",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);
                if (cnm == null)
                {
                    log?.Invoke("[NodeInserter] EnsureCustomNodeLoaded — CustomNodeManager not found (tried Public+NonPublic)");
                    return false;
                }

                var containsGuid = cnm.GetType().GetMethod("Contains",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Guid) }, null);
                if (containsGuid?.Invoke(cnm, new object[] { guid }) is true)
                {
                    log?.Invoke($"[NodeInserter] EnsureCustomNodeLoaded — already registered, guid={guid}");
                    return true;
                }

                var addMethod = cnm.GetType().GetMethod(
                    "AddUninitializedCustomNode",
                    BindingFlags.Public | BindingFlags.Instance);
                log?.Invoke($"[NodeInserter] EnsureCustomNodeLoaded — AddUninitializedCustomNode found={addMethod != null}");
                if (addMethod != null)
                {
                    var args = new object?[] { dyfPath, false, null };
                    try
                    {
                        var ok = addMethod.Invoke(cnm, args) as bool? ?? false;
                        log?.Invoke($"[NodeInserter] EnsureCustomNodeLoaded — AddUninitializedCustomNode returned={ok}");
                        if (ok) return true;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[NodeInserter] EnsureCustomNodeLoaded — AddUninitializedCustomNode threw: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[NodeInserter] EnsureCustomNodeLoaded EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
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
            double x,
            double y,
            Action<string>? log)
        {
            try
            {
                var cmdType = FindType("Dynamo.Models.DynamoModel+CreateNodeCommand");
                if (cmdType == null)
                {
                    log?.Invoke("[NodeInserter] ExecuteCreateNode — CreateNodeCommand type not found in AppDomain");
                    return null;
                }

                var ctor = cmdType.GetConstructor(new[]
                {
                    typeof(string), typeof(string),
                    typeof(double), typeof(double),
                    typeof(bool),   typeof(bool)
                });
                if (ctor == null)
                {
                    log?.Invoke("[NodeInserter] ExecuteCreateNode — matching CreateNodeCommand constructor not found");
                    return null;
                }

                var newGuid = Guid.NewGuid().ToString();
                log?.Invoke($"[NodeInserter] ExecuteCreateNode — firing CreateNodeCommand(nodeId={newGuid}, nodeName='{nodeTypeName}', x={x}, y={y})");
                var cmd = ctor.Invoke(new object[] { newGuid, nodeTypeName, x, y, false, false });

                var executeMethod = dynamoModel.GetType()
                    .GetMethod("ExecuteCommand", BindingFlags.Public | BindingFlags.Instance);
                if (executeMethod == null)
                {
                    log?.Invoke("[NodeInserter] ExecuteCreateNode — ExecuteCommand method not found on model");
                    return null;
                }

                executeMethod.Invoke(dynamoModel, new[] { cmd });
                log?.Invoke("[NodeInserter] ExecuteCreateNode — ExecuteCommand returned, scanning workspace.Nodes for GUID match");

                var nodesEnumerable = workspace.GetType()
                    .GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(workspace) as System.Collections.IEnumerable;

                if (nodesEnumerable == null)
                {
                    log?.Invoke("[NodeInserter] ExecuteCreateNode — workspace.Nodes is null");
                    return null;
                }

                int nodeCount = 0;
                foreach (var n in nodesEnumerable)
                {
                    if (n == null) continue;
                    nodeCount++;
                    var guidVal = n.GetType()
                        .GetProperty("GUID",
                            BindingFlags.Public | BindingFlags.Instance |
                            BindingFlags.FlattenHierarchy)
                        ?.GetValue(n);
                    if (guidVal?.ToString() == newGuid)
                    {
                        log?.Invoke($"[NodeInserter] ExecuteCreateNode — node found by GUID after scanning {nodeCount} node(s)");
                        return n;
                    }
                }

                log?.Invoke($"[NodeInserter] ExecuteCreateNode — GUID not found after scanning {nodeCount} node(s) — command likely failed silently");
                return null;
            }
            catch (Exception ex)
            {
                // TargetInvocationException wraps the real Dynamo exception — unwrap the chain.
                var e = ex;
                while (e.InnerException != null) e = e.InnerException;
                log?.Invoke($"[NodeInserter] ExecuteCreateNode EXCEPTION — outer={ex.GetType().Name}: {ex.Message}");
                if (!ReferenceEquals(e, ex))
                    log?.Invoke($"[NodeInserter] ExecuteCreateNode INNER CAUSE — {e.GetType().Name}: {e.Message}");
                return null;
            }
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
