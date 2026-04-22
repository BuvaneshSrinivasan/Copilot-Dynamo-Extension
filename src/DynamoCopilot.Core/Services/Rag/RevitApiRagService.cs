using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace DynamoCopilot.Core.Services.Rag
{
    /// <summary>
    /// Indexes RevitAPI.xml, RevitAPIUI.xml, and RevitAPIIFC.xml from the local Revit
    /// installation using BM25 search, then injects relevant API context into each
    /// code generation prompt.
    ///
    /// The BM25 index is built lazily on the first call and cached for the process lifetime.
    /// Fails silently (returns null) if RevitAPI.xml is not found — chat continues without RAG.
    /// </summary>
    public sealed class RevitApiRagService : IRevitRagService
    {
        private static readonly string[] XmlFileNames =
        {
            "RevitAPI.xml",
            "RevitAPIUI.xml",
            "RevitAPIIFC.xml",
        };

        private const int TopK              = 6;
        private const int MaxChunkChars     = 2500;
        private const int MaxMembersPerClass = 40;

        private readonly string? _overridePath;

        // Lazy<Task<...>> ensures the index is built exactly once across concurrent first calls.
        private readonly Lazy<Task<BM25Engine?>> _engineLazy;

        public RevitApiRagService(string? xmlOverridePath = null)
        {
            _overridePath = xmlOverridePath;
            _engineLazy = new Lazy<Task<BM25Engine?>>(
                BuildEngineAsync,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public async Task<string?> FetchContextAsync(string userQuery, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userQuery)) return null;
            try
            {
                var engine = await _engineLazy.Value.ConfigureAwait(false);
                if (engine == null) return null;

                var hits = engine.Search(userQuery, TopK);
                if (hits.Count == 0) return null;

                var sb = new StringBuilder();
                foreach (var chunk in hits)
                {
                    sb.AppendLine($"--- {chunk.Namespace}.{chunk.ClassName} ---");
                    sb.AppendLine(chunk.DisplayText);
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private async Task<BM25Engine?> BuildEngineAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string? xmlDir = ResolveXmlDirectory();
                    if (xmlDir == null) return null;

                    var chunks = ParseXmlDirectory(xmlDir);
                    if (chunks.Count == 0) return null;

                    return new BM25Engine(chunks);
                }
                catch
                {
                    return null;
                }
            }).ConfigureAwait(false);
        }

        private string? ResolveXmlDirectory()
        {
            // 1. User-supplied override path
            if (!string.IsNullOrWhiteSpace(_overridePath))
            {
                string? dir = ResolveDir(_overridePath!);
                if (dir != null) return dir;
            }

            // 2. Directory of the already-loaded RevitAPI assembly
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, "RevitAPI", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(asm.Location);
                        if (dir != null && File.Exists(Path.Combine(dir, "RevitAPI.xml")))
                            return dir;
                    }
                    catch { }
                }
            }

            // 3. Scan standard Autodesk install paths (newest first)
            string[] years = { "2027", "2026", "2025", "2024", "2023", "2022" };
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string year in years)
            {
                string candidate = Path.Combine(programFiles, "Autodesk", $"Revit {year}");
                if (File.Exists(Path.Combine(candidate, "RevitAPI.xml")))
                    return candidate;
            }

            return null;
        }

        private static string? ResolveDir(string path)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "RevitAPI.xml")))
                return path;
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && File.Exists(Path.Combine(dir, "RevitAPI.xml")))
                return dir;
            return null;
        }

        private static List<RagChunk> ParseXmlDirectory(string xmlDir)
        {
            // Group members by class, then emit one chunk per class.
            var byClass = new Dictionary<string, ClassAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (string fileName in XmlFileNames)
            {
                string path = Path.Combine(xmlDir, fileName);
                if (!File.Exists(path)) continue;
                try
                {
                    ParseXmlFile(path, byClass);
                }
                catch { }
            }

            var chunks = new List<RagChunk>(byClass.Count);
            foreach (var acc in byClass.Values)
            {
                var chunk = ToChunk(acc);
                if (chunk != null) chunks.Add(chunk);
            }
            return chunks;
        }

        private static void ParseXmlFile(
            string path,
            Dictionary<string, ClassAccumulator> byClass)
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments         = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace       = false,
            };

            using var reader = XmlReader.Create(path, settings);
            string? currentName    = null;
            string? currentSummary = null;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "member")
                {
                    currentName    = reader.GetAttribute("name");
                    currentSummary = null;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "summary"
                         && currentName != null)
                {
                    currentSummary = reader.ReadElementContentAsString();
                    currentSummary = Normalize(currentSummary);
                    ProcessMember(currentName, currentSummary, byClass);
                    currentName    = null;
                    currentSummary = null;
                }
            }
        }

        private static void ProcessMember(
            string nameAttr,
            string summary,
            Dictionary<string, ClassAccumulator> byClass)
        {
            if (!TryParse(nameAttr, out string ns, out string className, out string memberName))
                return;

            string key = ns + "." + className;
            if (!byClass.TryGetValue(key, out var acc))
            {
                acc = new ClassAccumulator { Namespace = ns, ClassName = className };
                byClass[key] = acc;
            }

            if (string.IsNullOrEmpty(memberName))
            {
                acc.ClassSummary = summary;
            }
            else if (acc.Members.Count < MaxMembersPerClass)
            {
                char prefix = nameAttr.Length > 1 ? nameAttr[0] : ' ';
                string label = prefix == 'P' ? "[Property]" :
                               prefix == 'F' ? "[Field]"    :
                               prefix == 'E' ? "[Event]"    : string.Empty;
                acc.Members.Add($"{label} {memberName}: {summary}".Trim());
            }
        }

        private static RagChunk? ToChunk(ClassAccumulator acc)
        {
            if (string.IsNullOrEmpty(acc.ClassSummary) && acc.Members.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine($"[Class: {acc.ClassName}]");
            sb.AppendLine($"Namespace: {acc.Namespace}");
            if (!string.IsNullOrEmpty(acc.ClassSummary))
                sb.AppendLine($"Summary: {acc.ClassSummary}");
            if (acc.Members.Count > 0)
            {
                sb.AppendLine();
                foreach (var m in acc.Members) sb.AppendLine(m);
            }

            string display = sb.ToString();
            if (display.Length > MaxChunkChars)
                display = display.Substring(0, MaxChunkChars) + "\n[...truncated]";

            return new RagChunk
            {
                ClassName   = acc.ClassName,
                Namespace   = acc.Namespace,
                DisplayText = display,
                IndexText   = display + " " + acc.ClassName + " " + acc.Namespace
            };
        }

        private static readonly HashSet<string> KnownSubNs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DB", "UI", "Creation", "Structure", "Mechanical", "Plumbing",
                "Electrical", "Architecture", "Analysis", "IFC", "Macros",
                "Visual", "ApplicationServices", "Parameters", "Exceptions"
            };

        private static bool TryParse(
            string nameAttr,
            out string ns,
            out string className,
            out string memberName)
        {
            ns = className = memberName = string.Empty;
            if (nameAttr.Length < 3) return false;

            string full = nameAttr.Length > 2 && nameAttr[1] == ':'
                ? nameAttr.Substring(2)
                : nameAttr;

            int paren = full.IndexOf('(');
            if (paren >= 0) full = full.Substring(0, paren);

            const string revitPrefix = "Autodesk.Revit.";
            int pi = full.IndexOf(revitPrefix, StringComparison.Ordinal);
            if (pi < 0) return false;

            string[] parts = full.Substring(pi).Split('.');
            if (parts.Length < 4) return false;

            int classIdx = -1;
            for (int i = 2; i < parts.Length; i++)
            {
                if (!KnownSubNs.Contains(parts[i])) { classIdx = i; break; }
            }
            if (classIdx < 0) return false;

            ns        = string.Join(".", parts, 0, classIdx);
            className = parts[classIdx];
            memberName = classIdx + 1 < parts.Length ? parts[classIdx + 1] : string.Empty;
            if (memberName.StartsWith("get_", StringComparison.Ordinal))
                memberName = memberName.Substring(4);
            else if (memberName.StartsWith("set_", StringComparison.Ordinal))
                memberName = memberName.Substring(4);

            return !string.IsNullOrWhiteSpace(className);
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string t = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t;
        }

        private sealed class ClassAccumulator
        {
            public string Namespace    = string.Empty;
            public string ClassName    = string.Empty;
            public string ClassSummary = string.Empty;
            public List<string> Members = new List<string>();
        }
    }
}
