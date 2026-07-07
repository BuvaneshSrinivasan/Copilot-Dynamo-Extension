using System;
using System.Reflection;

namespace DynamoCopilot.GraphInterop
{
    /// <summary>
    /// Reads the running Revit version from the current process via reflection —
    /// no compile-time dependency on RevitServices/RevitAPI, same approach used
    /// by <see cref="GraphNodeReader"/> and <see cref="PythonNodeInterop"/>.
    /// Shared between the Copilot and Suggest Nodes extensions.
    /// </summary>
    public static class RevitEnvironmentInterop
    {
        /// <summary>Returns the Revit year (e.g. 2026), or null outside a Revit context.</summary>
        public static int? TryGetRevitYear(Action<string>? log = null)
        {
            try
            {
                var dmType = Type.GetType(
                    "RevitServices.Persistence.DocumentManager, RevitServices",
                    throwOnError: false);
                if (dmType == null) return null;

                var instance = dmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) return null;

                var uiApp = dmType.GetProperty("CurrentUIApplication",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
                if (uiApp == null) return null;

                var app = uiApp.GetType().GetProperty("Application",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(uiApp);
                if (app == null) return null;

                var versionNumber = app.GetType().GetProperty("VersionNumber",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(app) as string;

                return int.TryParse(versionNumber, out var year) ? year : null;
            }
            catch (Exception ex) { log?.Invoke($"TryGetRevitYear failed: {ex}"); return null; }
        }
    }
}
