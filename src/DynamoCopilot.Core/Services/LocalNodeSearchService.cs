using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Core.Services
{
    /// <summary>
    /// Searches the local node database (SQLite file bundled with the installer).
    ///
    /// Search strategy:
    ///   1. Vector similarity  — cosine distance between the query embedding and each
    ///      stored embedding. Requires <see cref="IEmbeddingService"/> to be provided.
    ///   2. BM25 keyword search — always available, used as fallback when the embedding
    ///      service is null or the embedding file doesn't exist.
    ///
    /// The SQLite file lives at:
    ///   %AppData%\DynamoCopilot\nodes.db
    ///
    /// It is written by DynamoCopilot.NodeIndexer and is expected to already exist
    /// when this service is used.  If it is absent, all searches return an empty list.
    /// </summary>
    public sealed class LocalNodeSearchService
    {
        // Default result cap
        private const int TopK = 100;

        private readonly string             _dbPath;
        private readonly IEmbeddingService? _embedder;

        // In-memory cache of all node records (loaded once on first search)
        private List<LocalNodeRecord>? _cache;
        private readonly object         _cacheLock = new object();

        public LocalNodeSearchService(IEmbeddingService? embedder = null)
        {
            _dbPath  = Path.Combine(DynamoCopilotSettings.AppDataDir, "nodes.db");
            _embedder = embedder;
        }

        /// <summary>
        /// Returns up to <paramref name="topK"/> node suggestions for <paramref name="query"/>.
        /// </summary>
        public async Task<IReadOnlyList<NodeSuggestion>> SearchAsync(
            string            query,
            int               topK  = TopK,
            CancellationToken ct    = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<NodeSuggestion>();

            var records = await LoadCacheAsync(ct).ConfigureAwait(false);
            if (records.Count == 0)              return Array.Empty<NodeSuggestion>();

            // ── 1. Try vector search ──────────────────────────────────────────
            if (_embedder != null)
            {
                try
                {
                    var queryVec = await _embedder.EmbedAsync(query, ct).ConfigureAwait(false);
                    return VectorSearch(records, queryVec, topK);
                }
                catch
                {
                    // Embedding failed — fall through to keyword search
                }
            }

            // ── 2. BM25 keyword fallback ──────────────────────────────────────
            return KeywordSearch(records, query, topK);
        }

        // ── Vector search ─────────────────────────────────────────────────────

        private static IReadOnlyList<NodeSuggestion> VectorSearch(
            List<LocalNodeRecord> records,
            float[]               queryVec,
            int                   topK)
        {
            var scored = new List<(float score, LocalNodeRecord record)>(records.Count);
            foreach (var r in records)
            {
                if (r.Embedding == null || r.Embedding.Length != queryVec.Length) continue;
                var score = CosineSimilarity(queryVec, r.Embedding);
                scored.Add((score, r));
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));

            var result = new List<NodeSuggestion>(topK);
            for (int i = 0; i < Math.Min(topK, scored.Count); i++)
                result.Add(ToSuggestion(scored[i].record, scored[i].score));
            return result;
        }

        // ── BM25 keyword search ───────────────────────────────────────────────

        private static IReadOnlyList<NodeSuggestion> KeywordSearch(
            List<LocalNodeRecord> records,
            string                query,
            int                   topK)
        {
            var terms  = Tokenize(query);
            if (terms.Count == 0) return Array.Empty<NodeSuggestion>();

            // Compute per-term document frequency for IDF
            var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records)
            {
                var docTerms = new HashSet<string>(Tokenize(BuildSearchText(r)),
                                                   StringComparer.OrdinalIgnoreCase);
                foreach (var t in terms)
                    if (docTerms.Contains(t)) { df.TryGetValue(t, out var c); df[t] = c + 1; }
            }

            int N = records.Count;
            const float k1 = 1.5f, b = 0.75f;

            // Average document length
            float avgLen = 0;
            foreach (var r in records) avgLen += Tokenize(BuildSearchText(r)).Count;
            avgLen /= Math.Max(1, N);

            var scored = new List<(float score, LocalNodeRecord record)>(records.Count);
            foreach (var r in records)
            {
                var docTokens = Tokenize(BuildSearchText(r));
                var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in docTokens)
                {
                    tf.TryGetValue(t, out var c);
                    tf[t] = c + 1;
                }

                float score = 0;
                float dl    = docTokens.Count;
                foreach (var term in terms)
                {
                    tf.TryGetValue(term, out var f);
                    df.TryGetValue(term, out var d);
                    if (d == 0) continue;

                    float idf  = (float)Math.Log((N - d + 0.5f) / (d + 0.5f) + 1f);
                    float tfN  = f * (k1 + 1f) / (f + k1 * (1f - b + b * dl / avgLen));
                    score += idf * tfN;
                }

                if (score > 0f) scored.Add((score, r));
            }

            scored.Sort((a, b2) => b2.score.CompareTo(a.score));

            // Normalise scores to [0, 1]
            float maxScore = scored.Count > 0 ? scored[0].score : 1f;

            var result = new List<NodeSuggestion>(topK);
            for (int i = 0; i < Math.Min(topK, scored.Count); i++)
                result.Add(ToSuggestion(scored[i].record, scored[i].score / maxScore));
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0f, normA = 0f, normB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot   += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            float denom = (float)Math.Sqrt(normA) * (float)Math.Sqrt(normB);
            return denom < 1e-8f ? 0f : dot / denom;
        }

        private static string BuildSearchText(LocalNodeRecord r)
            => $"{r.Name} {r.Category} {r.PackageName} {r.Description}";

        private static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var tokens = new List<string>();
            // Split on non-alphanumeric and also on CamelCase boundaries
            var raw = text.ToLowerInvariant()
                          .Split(new[] { ' ', '_', '-', '.', '/', '(', ')', '[', ']', ',' },
                                 StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in raw)
                if (word.Length > 1) tokens.Add(word);
            return tokens;
        }

        private static NodeSuggestion ToSuggestion(LocalNodeRecord r, float score)
            => new NodeSuggestion
            {
                Name        = r.Name,
                Category    = r.Category,
                PackageName = r.PackageName,
                Description = r.Description,
                InputPorts  = r.InputPorts  ?? Array.Empty<string>(),
                OutputPorts = r.OutputPorts ?? Array.Empty<string>(),
                NodeType    = r.NodeType,
                Score       = score,
                Reason      = string.Empty   // local search doesn't generate AI reasons
            };

        // ── SQLite loader ─────────────────────────────────────────────────────

        private async Task<List<LocalNodeRecord>> LoadCacheAsync(CancellationToken ct)
        {
            lock (_cacheLock)
                if (_cache != null) return _cache;

            if (!File.Exists(_dbPath))
                return new List<LocalNodeRecord>();

            // Load is done on a thread-pool thread to avoid blocking the UI
            var loaded = await Task.Run(() => LoadFromDb(_dbPath), ct).ConfigureAwait(false);

            lock (_cacheLock)
            {
                _cache = loaded;
                return _cache;
            }
        }

        /// <summary>
        /// Reads all node records from the SQLite database.
        /// We use raw ADO.NET so Core has no NuGet dependency on EF Core.
        /// The actual SQLite driver (Microsoft.Data.Sqlite) is referenced from
        /// the Extension project which adds it as a NuGet package.
        /// </summary>
        private static List<LocalNodeRecord> LoadFromDb(string dbPath)
        {
            // Dynamically resolve the SQLite connection type so Core doesn't
            // carry a direct assembly reference to Microsoft.Data.Sqlite.
            // The extension loads that assembly, so it will be resolvable at runtime.
            var records = new List<LocalNodeRecord>();
            try
            {
                var connType = Type.GetType(
                    "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
                    throwOnError: false);

                if (connType == null) return records;   // driver not loaded yet

                var conn    = Activator.CreateInstance(connType, $"Data Source={dbPath};Mode=ReadOnly");
                var openM   = connType.GetMethod("Open");
                var createM = connType.GetMethod("CreateCommand");
                openM!.Invoke(conn, null);

                var cmd    = createM!.Invoke(conn, null)!;
                var cmdT   = cmd.GetType();
                cmdT.GetProperty("CommandText")!.SetValue(cmd,
                    "SELECT Name, Category, PackageName, Description, " +
                    "InputPortsJson, OutputPortsJson, NodeType, EmbeddingJson FROM Nodes");

                var readerM = cmdT.GetMethod("ExecuteReader", Type.EmptyTypes);
                var reader  = readerM!.Invoke(cmd, null)!;
                var rType   = reader.GetType();
                var readM   = rType.GetMethod("Read");
                var getStrM = rType.GetMethod("GetString", new[] { typeof(int) });
                var isNullM = rType.GetMethod("IsDBNull",  new[] { typeof(int) });

                while ((bool)readM!.Invoke(reader, null)!)
                {
                    string GetStr(int col) => (string)getStrM!.Invoke(reader, new object[] { col })!;
                    bool   IsNull(int col) => (bool)isNullM!.Invoke(reader, new object[] { col })!;

                    var r = new LocalNodeRecord
                    {
                        Name        = GetStr(0),
                        Category    = IsNull(1) ? null : GetStr(1),
                        PackageName = GetStr(2),
                        Description = IsNull(3) ? null : GetStr(3),
                        NodeType    = GetStr(6)
                    };

                    if (!IsNull(4))
                    {
                        var ij = GetStr(4);
                        r.InputPorts  = JsonSerializer.Deserialize<string[]>(ij);
                    }
                    if (!IsNull(5))
                    {
                        var oj = GetStr(5);
                        r.OutputPorts = JsonSerializer.Deserialize<string[]>(oj);
                    }
                    if (!IsNull(7))
                    {
                        var ej = GetStr(7);
                        var raw = JsonSerializer.Deserialize<float[]>(ej);
                        r.Embedding = raw;
                    }

                    records.Add(r);
                }

                rType.GetMethod("Close")?.Invoke(reader, null);
                connType.GetMethod("Close")?.Invoke(conn, null);
            }
            catch
            {
                // If anything goes wrong reading the DB, return whatever we have
            }

            return records;
        }

        // ── Inner record type ─────────────────────────────────────────────────

        private sealed class LocalNodeRecord
        {
            public string   Name        { get; set; } = string.Empty;
            public string?  Category    { get; set; }
            public string   PackageName { get; set; } = string.Empty;
            public string?  Description { get; set; }
            public string[] InputPorts  { get; set; } = Array.Empty<string>();
            public string[] OutputPorts { get; set; } = Array.Empty<string>();
            public string   NodeType    { get; set; } = string.Empty;
            public float[]? Embedding   { get; set; }
        }
    }
}
