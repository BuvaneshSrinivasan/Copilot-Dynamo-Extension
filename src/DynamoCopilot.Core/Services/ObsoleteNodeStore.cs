using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Persists (PackageName, NodeName, RevitYear?) tuples for nodes that failed to
    /// insert. Obsolete entries are scoped to the Revit year in which the failure
    /// occurred, so a node that fails on Revit 2023 is not hidden on Revit 2022
    /// where it may work fine.
    ///
    /// File: %AppData%\DynamoCopilot\obsolete-nodes.json
    ///
    /// JSON format: array of string arrays.
    ///   2-element entry ["pkg","node"]        → legacy / globally obsolete (Year = null)
    ///   3-element entry ["pkg","node","2023"] → obsolete only on Revit 2023
    ///   3-element entry ["pkg","node",""]     → globally obsolete (Year = null)
    /// </summary>
    public sealed class ObsoleteNodeStore
    {
        private readonly string _filePath;
        private readonly int?   _currentRevitYear;
        private readonly HashSet<(string Pkg, string Node, int? Year)> _set;
        private readonly object _lock = new object();

        public ObsoleteNodeStore(int? currentRevitYear = null)
        {
            _currentRevitYear = currentRevitYear;
            _filePath         = Path.Combine(DynamoCopilotSettings.AppDataDir, "obsolete-nodes.json");
            _set              = Load();
        }

        public int? CurrentRevitYear => _currentRevitYear;

        /// <summary>
        /// Returns true when the node should be hidden for the current Revit version.
        /// Matches entries with no year (globally obsolete) or entries tagged with
        /// the current Revit year.
        /// </summary>
        public bool IsObsolete(string packageName, string nodeName)
        {
            lock (_lock)
            {
                if (_set.Contains((packageName, nodeName, null)))
                    return true;
                if (_currentRevitYear.HasValue &&
                    _set.Contains((packageName, nodeName, _currentRevitYear.Value)))
                    return true;
                return false;
            }
        }

        /// <summary>
        /// Marks the node obsolete for the current Revit year (or globally if no
        /// year was provided at construction). Persists immediately.
        /// </summary>
        public void MarkObsolete(string packageName, string nodeName)
        {
            lock (_lock)
            {
                if (_set.Add((packageName, nodeName, _currentRevitYear)))
                    Save();
            }
        }

        private HashSet<(string, string, int?)> Load()
        {
            var set = new HashSet<(string, string, int?)>();
            try
            {
                if (!File.Exists(_filePath)) return set;

                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<string[]>>(json);
                if (list == null) return set;

                foreach (var item in list)
                {
                    if (item == null || item.Length < 2) continue;

                    var pkg  = item[0];
                    var node = item[1];
                    int? year = null;

                    if (item.Length >= 3 && !string.IsNullOrEmpty(item[2]) &&
                        int.TryParse(item[2], out var y))
                        year = y;

                    set.Add((pkg, node, year));
                }
            }
            catch { }
            return set;
        }

        private void Save()
        {
            try
            {
                var list = new List<string[]>();
                foreach (var (pkg, node, year) in _set)
                    list.Add(new[] { pkg, node, year?.ToString() ?? "" });

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(list));
            }
            catch { }
        }
    }
}
