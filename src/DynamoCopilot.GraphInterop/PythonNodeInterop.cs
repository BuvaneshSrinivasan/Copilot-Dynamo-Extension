using System;
using System.Linq;
using System.Reflection;

namespace DynamoCopilot.GraphInterop
{
    /// <summary>
    /// Provides runtime access to Dynamo's Python Script node (PythonNode) via reflection.
    /// This avoids a hard compile-time dependency on DSIronPython / DSCPython assemblies,
    /// which differ between Dynamo 2.x and 3.x and may not be present at build time.
    ///
    /// All public members are safe to call even if no Python node is selected —
    /// they return null / false gracefully.
    /// </summary>
    public static class PythonNodeInterop
    {
        // Fully qualified type names used in known Dynamo versions
        private const string PythonNodeTypeName = "PythonNodeModels.PythonNode";

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Returns the first selected node that is a Python Script node,
        /// or null if nothing appropriate is selected.
        /// Accepts the Dynamo ViewModel or WorkspaceModel via duck typing (object).
        /// </summary>
        public static object? GetSelectedPythonNode(object dynamoCurrentWorkspaceViewModel)
        {
            if (dynamoCurrentWorkspaceViewModel == null) return null;

            try
            {
                // WorkspaceViewModel.Nodes is IEnumerable of NodeViewModel
                var nodesProperty = dynamoCurrentWorkspaceViewModel.GetType()
                    .GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance);
                if (nodesProperty == null) return null;

                var nodeVMs = nodesProperty.GetValue(dynamoCurrentWorkspaceViewModel) as System.Collections.IEnumerable;
                if (nodeVMs == null) return null;

                foreach (var nodeVm in nodeVMs)
                {
                    if (nodeVm == null) continue;

                    // Check IsSelected
                    var isSelectedProp = nodeVm.GetType().GetProperty("IsSelected");
                    if (isSelectedProp?.GetValue(nodeVm) is not true) continue;

                    // Get underlying NodeModel
                    var nodeModelProp = nodeVm.GetType().GetProperty("NodeModel");
                    var nodeModel = nodeModelProp?.GetValue(nodeVm);
                    if (nodeModel == null) continue;

                    if (IsPythonNode(nodeModel))
                        return nodeModel;
                }
            }
            catch (Exception)
            {
                // Silently degrade — this is a best-effort reflection call
            }

            return null;
        }

        /// <summary>
        /// Reads the Script property from a PythonNode model.
        /// Returns empty string if the node is null or property is not found.
        /// </summary>
        public static string GetScriptContent(object pythonNodeModel)
        {
            if (pythonNodeModel == null) return string.Empty;

            try
            {
                var scriptProp = FindScriptProperty(pythonNodeModel.GetType());
                return scriptProp?.GetValue(pythonNodeModel) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Sets the Script property on a PythonNode model.
        /// Returns true on success.
        /// </summary>
        public static bool SetScriptContent(object pythonNodeModel, string code)
        {
            if (pythonNodeModel == null || code == null) return false;

            try
            {
                var scriptProp = FindScriptProperty(pythonNodeModel.GetType());
                if (scriptProp == null || !scriptProp.CanWrite) return false;

                scriptProp.SetValue(pythonNodeModel, code);

                // Tell Dynamo the node was modified so it re-executes visually
                var onModified = pythonNodeModel.GetType()
                    .GetMethod("OnNodeModified",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? pythonNodeModel.GetType()
                        .GetMethod("MarkNodeAsModified",
                            BindingFlags.Public | BindingFlags.Instance);
                onModified?.Invoke(pythonNodeModel, new object[] { true });

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the engine name of the Python node ("IronPython2", "CPython3", "PythonNet3").
        /// Returns "IronPython2" as a safe default if it cannot be determined.
        /// </summary>
        public static string GetEngineName(object pythonNodeModel)
        {
            if (pythonNodeModel == null) return "IronPython2";

            try
            {
                var engineProp = pythonNodeModel.GetType()
                    .GetProperty("EngineName", BindingFlags.Public | BindingFlags.Instance);
                return engineProp?.GetValue(pythonNodeModel) as string ?? "IronPython2";
            }
            catch
            {
                return "IronPython2";
            }
        }

        /// <summary>
        /// Returns true if the given node model is a Dynamo Python Script node.
        /// </summary>
        public static bool IsPythonNode(object nodeModel)
        {
            if (nodeModel == null) return false;
            var type = nodeModel.GetType();
            // Walk type hierarchy to find PythonNode
            while (type != null)
            {
                if (type.FullName == PythonNodeTypeName)
                    return true;
                type = type.BaseType;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------

        private static PropertyInfo? FindScriptProperty(Type nodeType)
        {
            // Property is named "Script" in PythonNodeModels
            return nodeType.GetProperty("Script", BindingFlags.Public | BindingFlags.Instance)
                ?? nodeType.GetProperty("Code", BindingFlags.Public | BindingFlags.Instance);
        }
    }
}
