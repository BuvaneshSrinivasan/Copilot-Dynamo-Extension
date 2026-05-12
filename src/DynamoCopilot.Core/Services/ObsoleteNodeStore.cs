using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Persists (PackageName, NodeName) pairs that failed to insert — meaning the node
    /// no longer exists in the installed version of the package. Obsolete nodes are filtered
    /// out of future search results and their Insert button is permanently disabled.
    /// File: %AppData%\DynamoCopilot\obsolete-nodes.json
    /// </summary>
    public sealed class ObsoleteNodeStore
    {
        private readonly string _filePath;
        private readonly HashSet<(string Pkg, string Node)> _set;
        private readonly object _lock = new object();

        public ObsoleteNodeStore()
        {
            _filePath = Path.Combine(DynamoCopilotSettings.AppDataDir, "obsolete-nodes.json");
            _set = Load();
        }

        public bool IsObsolete(string packageName, string nodeName)
        {
            lock (_lock)
                return _set.Contains((packageName, nodeName));
        }

        public void MarkObsolete(string packageName, string nodeName)
        {
            lock (_lock)
            {
                if (_set.Add((packageName, nodeName)))
                    Save();
            }
        }

        private HashSet<(string, string)> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new HashSet<(string, string)>();

                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<string[]>>(json);
                var set  = new HashSet<(string, string)>();
                if (list != null)
                    foreach (var item in list)
                        if (item != null && item.Length == 2)
                            set.Add((item[0], item[1]));
                return set;
            }
            catch
            {
                return new HashSet<(string, string)>();
            }
        }

        private void Save()
        {
            try
            {
                var list = new List<string[]>();
                foreach (var (pkg, node) in _set)
                    list.Add(new[] { pkg, node });

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(list));
            }
            catch { }
        }
    }
}
