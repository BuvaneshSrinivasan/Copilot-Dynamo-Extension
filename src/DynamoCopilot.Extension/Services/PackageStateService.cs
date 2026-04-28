using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Scans all installed Dynamo version package folders (Revit + Core, every version)
    /// and answers IsInstalled queries.  Call Refresh() after a package download.
    /// </summary>
    public sealed class PackageStateService
    {
        // All packages found across every Dynamo version folder (used for path resolution).
        private readonly HashSet<string>            _installed
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Packages found only under the currently running Dynamo version's packages folder.
        private readonly HashSet<string>            _installedCurrentVersion
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _paths
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The packages directory for the currently running Dynamo version.
        /// This is where new downloads will be extracted.
        /// Null when it could not be determined.
        /// </summary>
        public string? CurrentVersionPackagesDir { get; }

        public PackageStateService(string? currentVersionPackagesDir)
        {
            CurrentVersionPackagesDir = currentVersionPackagesDir;
            Refresh();
        }

        /// <summary>
        /// Returns true only if the package is installed under the currently running
        /// Dynamo version's packages folder. A package installed for a different Dynamo
        /// version (e.g. downloaded in Revit 2025 but now running in Revit 2024) is not
        /// considered installed.
        /// </summary>
        public bool IsInstalled(string packageName)
        {
            if (CurrentVersionPackagesDir != null)
                return _installedCurrentVersion.Contains(packageName);
            // CurrentVersionPackagesDir unknown — fall back to any-version check.
            return _installed.Contains(packageName);
        }

        /// <summary>Returns the full path to the package folder, or null if not installed.</summary>
        public string? GetPackageFolderPath(string packageName)
            => _paths.TryGetValue(packageName, out var p) ? p : null;

        /// <summary>Fired after every <see cref="Refresh"/> call. All card VMs subscribe to this.</summary>
        public event Action? Refreshed;

        /// <summary>Re-scans all package folders.  Call after a successful download.</summary>
        public void Refresh()
        {
            _installed.Clear();
            _installedCurrentVersion.Clear();
            _paths.Clear();

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var roots = new[]
            {
                Path.Combine(appData, "Dynamo", "Dynamo Revit"),
                Path.Combine(appData, "Dynamo", "Dynamo Core"),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var versionDir in Directory.EnumerateDirectories(root))
                {
                    var packagesDir = Path.Combine(versionDir, "packages");
                    if (!Directory.Exists(packagesDir)) continue;

                    foreach (var pkgDir in Directory.EnumerateDirectories(packagesDir))
                    {
                        var name = Path.GetFileName(pkgDir);
                        if (string.IsNullOrEmpty(name)) continue;

                        _installed.Add(name);

                        // Prefer the current-version path if available; otherwise first found.
                        if (!_paths.ContainsKey(name))
                            _paths[name] = pkgDir;

                        if (CurrentVersionPackagesDir != null &&
                            packagesDir.Equals(CurrentVersionPackagesDir,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            _installedCurrentVersion.Add(name);
                            _paths[name] = pkgDir; // current version wins
                        }
                    }
                }
            }

            Refreshed?.Invoke();
        }
    }
}
