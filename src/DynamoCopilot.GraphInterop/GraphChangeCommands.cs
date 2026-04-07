using System;
using System.Diagnostics;
using System.Reflection;

namespace DynamoCopilot.GraphInterop
{
    /// <summary>
    /// Issues undo-safe graph modification commands via Dynamo's command infrastructure.
    /// Uses reflection to avoid compile-time dependency on specific Dynamo assembly versions.
    /// </summary>
    public static class GraphChangeCommands
    {
        // -----------------------------------------------------------------
        // Update existing Python Script node
        // -----------------------------------------------------------------

        /// <summary>
        /// Updates the Script property of a PythonNode via a Dynamo model command
        /// so the action is undo-able (Ctrl+Z).
        ///
        /// Falls back to direct property set if the command infrastructure isn't available.
        /// </summary>
        /// <param name="dynamoModel">The DynamoModel instance (object to avoid hard ref).</param>
        /// <param name="pythonNodeModel">The PythonNode model returned by PythonNodeInterop.</param>
        /// <param name="code">New Python code to apply.</param>
        /// <param name="workspaceViewModel">The WorkspaceViewModel — used to clear the visual
        /// ErrorBubble on the NodeViewModel directly, bypassing Dynamo's State-guard in
        /// Logic_NodeMessagesClearing which skips clearing when State == Error.</param>
        public static bool UpdatePythonNodeScript(object dynamoModel, object pythonNodeModel, string code,
            object? workspaceViewModel = null)
        {
            if (dynamoModel == null || pythonNodeModel == null || code == null)
                return false;

            // Delete the old node and recreate a fresh one at the same canvas position.
            // Updating in place leaves Dynamo's internal Warning infos in the backing 'infos'
            // field — ClearRuntimeError() (called before every run) intentionally skips
            // Warning entries, so the old warning re-appears after any successful run.
            // A fresh node has no history, no stale state, no stacking.
            var newNode = RecreatePythonNode(dynamoModel, pythonNodeModel, code);
            return newNode != null;
        }

        /// <summary>
        /// Deletes <paramref name="oldNodeModel"/> from the current workspace and creates a
        /// brand-new PythonNode at the same canvas position with <paramref name="code"/> set.
        /// This avoids all Dynamo internal warning/state accumulation that survives in-place
        /// updates regardless of how aggressively we clear infos fields or ErrorBubble.
        /// </summary>
        private static object? RecreatePythonNode(object dynamoModel, object oldNodeModel, string code)
        {
            Debugger.Launch();
            try
            {
                // 1. Capture canvas position before removing
                double x = 0, y = 0;
                try
                {
                    var xProp = oldNodeModel.GetType().GetProperty("X",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    var yProp = oldNodeModel.GetType().GetProperty("Y",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (xProp != null) x = Convert.ToDouble(xProp.GetValue(oldNodeModel));
                    if (yProp != null) y = Convert.ToDouble(yProp.GetValue(oldNodeModel));
                }
                catch { }

                // 2. Get the WorkspaceModel
                var workspaceProp = dynamoModel.GetType()
                    .GetProperty("CurrentWorkspace", BindingFlags.Public | BindingFlags.Instance);
                var workspace = workspaceProp?.GetValue(dynamoModel);
                if (workspace == null) return null;

                // 3. Remove the old node.
                // RemoveAndDisposeNode(NodeModel, bool undoEntry = true) — default params are
                // invisible to reflection, so we must match the overload and supply every arg.
                bool removed = false;
                foreach (var name in new[] { "RemoveAndDisposeNode", "RemoveNode" })
                {
                    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                    foreach (var m in workspace.GetType().GetMethods(flags))
                    {
                        if (m.Name != name) continue;
                        var prms = m.GetParameters();
                        try
                        {
                            if (prms.Length == 1)
                                m.Invoke(workspace, new[] { oldNodeModel });
                            else if (prms.Length == 2)
                                m.Invoke(workspace, new object[] { oldNodeModel, true });
                            else
                                continue;
                            removed = true;
                            break;
                        }
                        catch { }
                    }
                    if (removed) break;
                }

                // 4. Create a fresh node — no history, no stale warnings
                var newNode = ExecuteCreateNodeCommand(dynamoModel, workspace, "PythonNodeModels.PythonNode");
                if (newNode == null) return null;

                // 5. Restore canvas position
                try
                {
                    var xProp = newNode.GetType().GetProperty("X",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    var yProp = newNode.GetType().GetProperty("Y",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    xProp?.SetValue(newNode, x);
                    yProp?.SetValue(newNode, y);
                }
                catch { }

                // 6. Write the code to the fresh node
                bool written = TryDirectUpdateValue(newNode, code);
                if (!written)
                    PythonNodeInterop.SetScriptContent(newNode, code);

                return newNode;
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------
        // Create a new Python Script node
        // -----------------------------------------------------------------

        /// <summary>
        /// Creates a new Python Script node in the current workspace via DynamoModel.
        /// The node is placed at the center of the visible canvas.
        /// Returns the created node model or null on failure.
        /// </summary>
        public static object? CreatePythonNode(object dynamoModel, string initialCode)
        {
            if (dynamoModel == null) return null;

            try
            {
                // DynamoModel.CurrentWorkspace is the active WorkspaceModel
                var workspaceProp = dynamoModel.GetType()
                    .GetProperty("CurrentWorkspace", BindingFlags.Public | BindingFlags.Instance);
                var workspace = workspaceProp?.GetValue(dynamoModel);
                if (workspace == null) return null;

                // Use CreateNodeCommand approach to add a Python node
                // Node name in Dynamo's node library: "Python Script" → type "PythonNodeModels.PythonNode"
                const string pythonNodeTypeName = "PythonNodeModels.PythonNode";

                // Try to call DynamoModel.ExecuteCommand with a CreateNodeCommand
                var nodeModel = ExecuteCreateNodeCommand(dynamoModel, workspace, pythonNodeTypeName);

                if (nodeModel != null && !string.IsNullOrEmpty(initialCode))
                    UpdatePythonNodeScript(dynamoModel, nodeModel, initialCode);

                return nodeModel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // -----------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Calls nodeModel.UpdateValue(UpdateValueParams) directly.
        /// This is what UpdateModelValueCommand does internally — avoids dispatcher issues.
        /// </summary>
        private static bool TryDirectUpdateValue(object nodeModel, string code)
        {
            try
            {
                // UpdateValueParams is in Dynamo.Graph.Nodes namespace
                var paramsType = FindType("Dynamo.Graph.Nodes.UpdateValueParams");
                if (paramsType == null) return false;

                // Constructor: (string propertyName, string propertyValue)
                var ctor = paramsType.GetConstructor(new[] { typeof(string), typeof(string) });
                if (ctor == null) return false;

                var updateMethod = nodeModel.GetType()
                    .GetMethod("UpdateValue", BindingFlags.Public | BindingFlags.Instance);
                if (updateMethod == null) return false;

                // Try "ScriptContent" first (the name PythonNode.UpdateValue handles)
                var p1 = ctor.Invoke(new object[] { "ScriptContent", code });
                var result = updateMethod.Invoke(nodeModel, new[] { p1 });
                if (result is true) return true;

                // Some versions use "Code"
                var p2 = ctor.Invoke(new object[] { "Code", code });
                result = updateMethod.Invoke(nodeModel, new[] { p2 });
                return result is true;
            }
            catch
            {
                return false;
            }
        }

        private static object? ExecuteCreateNodeCommand(
            object dynamoModel, object workspace, string nodeTypeName)
        {
            try
            {
                // NodeFactory lives on DynamoModel, not on WorkspaceModel
                var factory = dynamoModel.GetType()
                    .GetProperty("NodeFactory", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dynamoModel);

                if (factory == null) return null;

                // NodeFactory.CreateNodeFromTypeName(string) — the standard API
                var createMethod =
                    factory.GetType().GetMethod("CreateNodeFromTypeName",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?? factory.GetType().GetMethod("CreateNodeByName",
                        BindingFlags.Public | BindingFlags.Instance);

                if (createMethod == null) return null;

                var node = createMethod.Invoke(factory, new object[] { nodeTypeName });
                if (node == null) return null;

                // Add node to workspace — AddAndSelectNode(NodeModel) takes only the node
                var addNodeMethod =
                    workspace.GetType().GetMethod("AddAndSelectNode",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?? workspace.GetType().GetMethod("AddNode",
                        BindingFlags.Public | BindingFlags.Instance);

                if (addNodeMethod != null)
                {
                    var prms = addNodeMethod.GetParameters();
                    if (prms.Length == 1)
                        addNodeMethod.Invoke(workspace, new[] { node });
                    else if (prms.Length == 2)
                        addNodeMethod.Invoke(workspace, new object[] { node, false });
                }

                return node;
            }
            catch
            {
                return null;
            }
        }

        private static Type? FindType(string fullTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullTypeName, throwOnError: false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
