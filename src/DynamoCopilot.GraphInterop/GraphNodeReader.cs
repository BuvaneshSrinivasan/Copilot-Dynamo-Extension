using System;
using System.Collections.Generic;
using System.Reflection;

namespace DynamoCopilot.GraphInterop
{
    /// <summary>
    /// Reads the display names of all nodes currently in the Dynamo workspace.
    ///
    /// Used by the "Suggest Nodes" feature: the names are sent to the server as
    /// <c>GraphContext</c> so Gemini can tailor its re-ranking to what the user
    /// already has in their graph (avoiding duplicates, suggesting complementary nodes).
    ///
    /// All access is via reflection so there is no hard compile-time dependency on
    /// Dynamo internals — the same approach used by <see cref="PythonNodeInterop"/>.
    /// </summary>
    public static class GraphNodeReader
    {
        /// <summary>
        /// Returns the display name (NickName) of every node in the workspace.
        /// Falls back to the node's technical Name if NickName is blank.
        /// Returns an empty list when the workspace view model is null or reflection fails.
        /// </summary>
        public static IReadOnlyList<string> GetAllNodeNames(object? workspaceViewModel)
        {
            if (workspaceViewModel == null)
                return Array.Empty<string>();

            try
            {
                // WorkspaceViewModel.Nodes is IEnumerable<NodeViewModel>
                var nodesProp = workspaceViewModel.GetType()
                    .GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance);

                if (nodesProp?.GetValue(workspaceViewModel) is not System.Collections.IEnumerable nodeVMs)
                    return Array.Empty<string>();

                var names = new List<string>();

                foreach (var nodeVm in nodeVMs)
                {
                    if (nodeVm == null) continue;

                    // Each NodeViewModel exposes NodeModel (the underlying model object)
                    var nodeModelProp = nodeVm.GetType()
                        .GetProperty("NodeModel", BindingFlags.Public | BindingFlags.Instance);
                    var nodeModel = nodeModelProp?.GetValue(nodeVm);
                    if (nodeModel == null) continue;

                    // NickName is what the user sees (and can rename).
                    // Name is the package-defined technical name — use it as fallback.
                    var nick = GetStringProp(nodeModel, "NickName");
                    var name = string.IsNullOrWhiteSpace(nick)
                        ? GetStringProp(nodeModel, "Name")
                        : nick;

                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }

                return names;
            }
            catch
            {
                // Best-effort — never crash the extension due to a graph read failure
                return Array.Empty<string>();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string GetStringProp(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType()
                    .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(obj) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
