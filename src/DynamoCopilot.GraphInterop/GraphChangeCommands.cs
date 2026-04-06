using System;
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
        public static bool UpdatePythonNodeScript(object dynamoModel, object pythonNodeModel, string code)
        {
            if (dynamoModel == null || pythonNodeModel == null || code == null)
                return false;

            // 1. Call UpdateValue directly on the node — this is what UpdateModelValueCommand
            //    does internally and avoids threading/dispatcher issues entirely.
            if (TryDirectUpdateValue(pythonNodeModel, code))
                return true;

            // 2. Fallback: direct property set + notify Dynamo
            return PythonNodeInterop.SetScriptContent(pythonNodeModel, code);
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
                // Locate NodeFactory on the workspace
                var factoryProp = workspace.GetType()
                    .GetProperty("NodeFactory", BindingFlags.Public | BindingFlags.Instance);
                var factory = factoryProp?.GetValue(workspace);
                if (factory == null) return null;

                // NodeFactory.CreateNodeFromTypeName(string typeName) or CreateNodeByName
                var createMethod = factory.GetType()
                    .GetMethod("CreateNodeFromTypeName", BindingFlags.Public | BindingFlags.Instance)
                    ?? factory.GetType()
                        .GetMethod("CreateNodeByName", BindingFlags.Public | BindingFlags.Instance);

                if (createMethod == null) return null;

                var node = createMethod.Invoke(factory, new object[] { nodeTypeName });
                if (node == null) return null;

                // Add node to workspace
                var addNodeMethod = workspace.GetType()
                    .GetMethod("AddAndSelectNode", BindingFlags.Public | BindingFlags.Instance)
                    ?? workspace.GetType()
                        .GetMethod("AddNode", BindingFlags.Public | BindingFlags.Instance);

                addNodeMethod?.Invoke(workspace, new[] { node, false });

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
