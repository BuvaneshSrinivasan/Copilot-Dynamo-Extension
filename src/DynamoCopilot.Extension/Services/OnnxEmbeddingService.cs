using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Settings;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Produces 384-dim sentence embeddings using a bundled all-MiniLM-L6-v2 ONNX model.
    ///
    /// Model file:  %AppData%\DynamoCopilot\models\all-MiniLM-L6-v2.onnx
    /// Vocab file:  %AppData%\DynamoCopilot\models\vocab.txt
    ///
    /// Both files are copied there by the installer.
    /// </summary>
    public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
    {
        public int Dimension => 384;

        private static readonly string ModelsDir =
            Path.Combine(DynamoCopilotSettings.AppDataDir, "models");

        private static readonly string ModelPath =
            Path.Combine(ModelsDir, "model.onnx");

        private static readonly string VocabPath =
            Path.Combine(ModelsDir, "vocab.txt");

        private readonly InferenceSession?     _session;
        private readonly BertTokenizer?        _tokenizer;
        private readonly bool                  _ready;

        public OnnxEmbeddingService()
        {
            if (!File.Exists(ModelPath) || !File.Exists(VocabPath))
                return;

            try
            {
                var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                _session   = new InferenceSession(ModelPath, opts);
                _tokenizer = new BertTokenizer(VocabPath);
                _ready     = true;
            }
            catch
            {
                _ready = false;
            }
        }

        public bool IsReady => _ready;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            if (!_ready || _session == null || _tokenizer == null)
                throw new InvalidOperationException(
                    "ONNX embedding model is not available. " +
                    "Ensure the model files are present in the DynamoCopilot models folder.");

            // Run on thread pool to keep UI responsive
            return Task.Run(() => Embed(text), ct);
        }

        private float[] Embed(string text)
        {
            const int maxLen = 128;

            var (inputIds, attentionMask, tokenTypeIds) = _tokenizer!.Encode(text, maxLen);

            var ids    = new DenseTensor<long>(inputIds.Select(x => (long)x).ToArray(),   new[] { 1, inputIds.Length });
            var mask   = new DenseTensor<long>(attentionMask.Select(x => (long)x).ToArray(), new[] { 1, attentionMask.Length });
            var types  = new DenseTensor<long>(tokenTypeIds.Select(x => (long)x).ToArray(),  new[] { 1, tokenTypeIds.Length });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids",      ids),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", types)
            };

            using var results = _session!.Run(inputs);

            // all-MiniLM-L6-v2 outputs "last_hidden_state": [1, seq_len, 384]
            // Mean-pool over sequence length (ignoring padding tokens)
            var hidden = results.First(r => r.Name == "last_hidden_state")
                                .AsTensor<float>();

            int seqLen = inputIds.Length;
            var pooled = new float[Dimension];
            int count  = 0;

            for (int i = 0; i < seqLen; i++)
            {
                if (attentionMask[i] == 0) continue;
                for (int j = 0; j < Dimension; j++)
                    pooled[j] += hidden[0, i, j];
                count++;
            }

            if (count > 0)
                for (int j = 0; j < Dimension; j++)
                    pooled[j] /= count;

            // L2-normalise
            float norm = 0f;
            for (int j = 0; j < Dimension; j++) norm += pooled[j] * pooled[j];
            norm = (float)Math.Sqrt(norm);
            if (norm > 1e-8f)
                for (int j = 0; j < Dimension; j++) pooled[j] /= norm;

            return pooled;
        }

        public void Dispose() => _session?.Dispose();
    }

    // ── Minimal BERT WordPiece tokenizer ──────────────────────────────────────
    // Handles the vocab.txt that ships with all-MiniLM-L6-v2.
    // Only implements what's needed for English sentence embedding — no language
    // detection, no Chinese character splitting, no accent stripping.

    internal sealed class BertTokenizer
    {
        private readonly Dictionary<string, int> _vocab;

        private const int ClsId = 101;   // [CLS]
        private const int SepId = 102;   // [SEP]
        private const int UNKId = 100;   // [UNK]

        public BertTokenizer(string vocabPath)
        {
            _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            int idx = 0;
            foreach (var line in File.ReadLines(vocabPath))
                _vocab[line.Trim()] = idx++;
        }

        /// <summary>
        /// Returns (inputIds, attentionMask, tokenTypeIds) — each array has length == maxLen.
        /// </summary>
        public (int[] inputIds, int[] attentionMask, int[] tokenTypeIds) Encode(
            string text, int maxLen)
        {
            var wordPieceIds = new List<int> { ClsId };

            foreach (var word in BasicTokenize(text))
            {
                var pieces = WordPiece(word.ToLowerInvariant());
                wordPieceIds.AddRange(pieces);
                if (wordPieceIds.Count >= maxLen - 1) break;
            }

            wordPieceIds.Add(SepId);

            // Truncate to maxLen
            if (wordPieceIds.Count > maxLen)
                wordPieceIds = wordPieceIds.GetRange(0, maxLen);

            int seqLen = wordPieceIds.Count;
            var inputIds      = new int[maxLen];
            var attentionMask = new int[maxLen];
            var tokenTypeIds  = new int[maxLen];   // all zeros for single-sentence

            for (int i = 0; i < seqLen; i++)
            {
                inputIds[i]      = wordPieceIds[i];
                attentionMask[i] = 1;
            }
            // padding tokens already have id=0, mask=0

            return (inputIds, attentionMask, tokenTypeIds);
        }

        // Split on whitespace and punctuation (basic BERT tokenisation)
        private static IEnumerable<string> BasicTokenize(string text)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                }
                else if (IsPunct(c))
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    tokens.Add(c.ToString());
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        private static bool IsPunct(char c)
            => char.IsPunctuation(c) || char.IsSymbol(c);

        // WordPiece sub-word segmentation
        private IEnumerable<int> WordPiece(string word)
        {
            if (_vocab.TryGetValue(word, out var id))
            {
                yield return id;
                yield break;
            }

            bool first   = true;
            int  start   = 0;
            bool foundAny = false;

            while (start < word.Length)
            {
                int   end  = word.Length;
                int   bestId = -1;
                int   bestEnd = start;

                while (start < end)
                {
                    var sub  = first ? word.Substring(start, end - start)
                                     : "##" + word.Substring(start, end - start);
                    if (_vocab.TryGetValue(sub, out var subId))
                    {
                        bestId  = subId;
                        bestEnd = end;
                        break;
                    }
                    end--;
                }

                if (bestId == -1)
                {
                    // No sub-word found — emit UNK for the whole word
                    yield return UNKId;
                    yield break;
                }

                yield return bestId;
                foundAny = true;
                first    = false;
                start    = bestEnd;
            }

            if (!foundAny) yield return UNKId;
        }
    }
}
