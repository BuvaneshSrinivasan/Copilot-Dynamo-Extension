using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DynamoCopilot.Core.Services.Rag
{
    // Pure C# BM25 (k1=1.5, b=0.75). No external dependencies.
    // Strengths: exact API name matching (FilteredElementCollector, BuiltInParameter, etc.)
    internal sealed class BM25Engine
    {
        private const double K1 = 1.5;
        private const double B  = 0.75;

        private readonly List<RagChunk> _chunks;
        private readonly Dictionary<string, List<(int idx, int tf)>> _index;
        private readonly int[]    _lengths;
        private readonly double   _avgLen;
        private readonly Dictionary<string, double> _idfCache;

        public int ChunkCount => _chunks.Count;

        public BM25Engine(List<RagChunk> chunks)
        {
            _chunks   = chunks ?? throw new ArgumentNullException("chunks");
            _index    = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
            _idfCache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _lengths  = new int[chunks.Count];
            BuildIndex();
            long total = 0;
            for (int i = 0; i < _lengths.Length; i++) total += _lengths[i];
            _avgLen = chunks.Count > 0 ? (double)total / chunks.Count : 1.0;
        }

        public List<RagChunk> Search(string query, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query) || _chunks.Count == 0)
                return new List<RagChunk>();

            var tokens = Tokenize(query);
            if (tokens.Count == 0) return new List<RagChunk>();

            var scores = new double[_chunks.Count];
            foreach (string token in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_index.TryGetValue(token, out var postings)) continue;
                double idf = GetIdf(token, postings.Count);
                foreach (var (idx, tf) in postings)
                {
                    double dl    = _lengths[idx];
                    double tfNorm = (tf * (K1 + 1.0)) /
                                   (tf + K1 * (1.0 - B + B * dl / _avgLen));
                    scores[idx] += idf * tfNorm;
                }
            }

            var results = new List<(int idx, double score)>();
            for (int i = 0; i < scores.Length; i++)
                if (scores[i] > 0) results.Add((i, scores[i]));

            results.Sort((a, b) => b.score.CompareTo(a.score));
            return results.Take(topK).Select(r => _chunks[r.idx]).ToList();
        }

        private void BuildIndex()
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                var tokens = Tokenize(_chunks[i].IndexText);
                _lengths[i] = tokens.Count;
                var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string t in tokens)
                    tf[t] = tf.TryGetValue(t, out int c) ? c + 1 : 1;
                foreach (var kv in tf)
                {
                    if (!_index.TryGetValue(kv.Key, out var list))
                    { list = new List<(int, int)>(); _index[kv.Key] = list; }
                    list.Add((i, kv.Value));
                }
            }
        }

        private double GetIdf(string token, int df)
        {
            if (_idfCache.TryGetValue(token, out double v)) return v;
            int n = _chunks.Count;
            double idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);
            _idfCache[token] = idf;
            return idf;
        }

        internal static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var raw    = Regex.Split(text, @"[^a-zA-Z0-9_]+");
            var result = new List<string>(raw.Length * 2);
            foreach (string token in raw)
            {
                if (token.Length < 2) continue;
                result.Add(token);
                var sub = SplitCamelCase(token);
                if (sub.Count > 1) result.AddRange(sub);
            }
            return result;
        }

        private static List<string> SplitCamelCase(string token)
        {
            var parts = Regex.Split(
                token,
                @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])");
            var result = new List<string>();
            foreach (string p in parts)
                if (p.Length >= 2) result.Add(p);
            return result;
        }
    }
}
