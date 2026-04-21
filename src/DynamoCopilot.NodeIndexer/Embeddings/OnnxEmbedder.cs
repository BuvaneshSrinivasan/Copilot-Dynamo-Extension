using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Embeddings;

// =============================================================================
// OnnxEmbedder — all-MiniLM-L6-v2 sentence embeddings (384-dim)
// =============================================================================
// Generates embeddings that are stored in the local SQLite export (nodes.db).
// The Extension uses the identical model at runtime via OnnxEmbeddingService
// so query vectors and document vectors are always comparable.
//
// Usage:
//   var embedder = new OnnxEmbedder("path/to/all-MiniLM-L6-v2.onnx", "path/to/vocab.txt");
//   float[] vec = await embedder.EmbedAsync("Python list comprehension");
//
// Model files live in assets/models/ in the repo root:
//   model.onnx  — from https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/tree/main/onnx
//   vocab.txt   — from https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/blob/main/vocab.txt
// =============================================================================

public sealed class OnnxEmbedder : IDisposable
{
    public const int Dimensions = 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizerLocal _tokenizer;

    public OnnxEmbedder(string modelPath, string vocabPath)
    {
        var opts = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        _session   = new InferenceSession(modelPath, opts);
        _tokenizer = new BertTokenizerLocal(vocabPath);
    }

    public Task<float[]?[]> EmbedBatchAsync(
        IReadOnlyList<NodeRecord> records,
        CancellationToken         ct = default)
    {
        // ONNX Runtime is not async-native, so run on thread pool to stay non-blocking
        return Task.Run(() =>
        {
            var results = new float[]?[records.Count];
            for (int i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try   { results[i] = EmbedOne(records[i].EmbeddingText); }
                catch { results[i] = null; }
            }
            return results;
        }, ct);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private float[] EmbedOne(string text)
    {
        const int maxLen = 128;
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, maxLen);

        var ids   = new DenseTensor<long>(inputIds.Select(x => (long)x).ToArray(),     new[] { 1, inputIds.Length });
        var mask  = new DenseTensor<long>(attentionMask.Select(x => (long)x).ToArray(), new[] { 1, attentionMask.Length });
        var types = new DenseTensor<long>(tokenTypeIds.Select(x => (long)x).ToArray(),  new[] { 1, tokenTypeIds.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",      ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types)
        };

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();

        var pooled = new float[Dimensions];
        int count  = 0;

        for (int i = 0; i < inputIds.Length; i++)
        {
            if (attentionMask[i] == 0) continue;
            for (int j = 0; j < Dimensions; j++)
                pooled[j] += hidden[0, i, j];
            count++;
        }

        if (count > 0)
            for (int j = 0; j < Dimensions; j++) pooled[j] /= count;

        // L2-normalise
        float norm = 0f;
        for (int j = 0; j < Dimensions; j++) norm += pooled[j] * pooled[j];
        norm = MathF.Sqrt(norm);
        if (norm > 1e-8f)
            for (int j = 0; j < Dimensions; j++) pooled[j] /= norm;

        return pooled;
    }

    public void Dispose() => _session.Dispose();
}

// ── Shared minimal BERT tokenizer (mirrors OnnxEmbeddingService.BertTokenizer) ──

internal sealed class BertTokenizerLocal
{
    private readonly Dictionary<string, int> _vocab;
    private const int ClsId = 101;
    private const int SepId = 102;
    private const int UNKId = 100;

    public BertTokenizerLocal(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        int idx = 0;
        foreach (var line in File.ReadLines(vocabPath))
            _vocab[line.Trim()] = idx++;
    }

    public (int[] inputIds, int[] attentionMask, int[] tokenTypeIds) Encode(string text, int maxLen)
    {
        var ids = new List<int> { ClsId };
        foreach (var word in BasicTokenize(text))
        {
            ids.AddRange(WordPiece(word.ToLowerInvariant()));
            if (ids.Count >= maxLen - 1) break;
        }
        ids.Add(SepId);
        if (ids.Count > maxLen) ids = ids.GetRange(0, maxLen);

        int seqLen        = ids.Count;
        var inputIds      = new int[maxLen];
        var attentionMask = new int[maxLen];
        var tokenTypeIds  = new int[maxLen];

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[i]      = ids[i];
            attentionMask[i] = 1;
        }
        return (inputIds, attentionMask, tokenTypeIds);
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        var tokens  = new List<string>();
        var current = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                tokens.Add(c.ToString());
            }
            else { current.Append(c); }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private IEnumerable<int> WordPiece(string word)
    {
        if (_vocab.TryGetValue(word, out var id)) { yield return id; yield break; }

        bool first = true;
        int  start = 0;
        while (start < word.Length)
        {
            int end = word.Length, bestId = -1, bestEnd = start;
            while (start < end)
            {
                var sub = first ? word.Substring(start, end - start)
                                : "##" + word.Substring(start, end - start);
                if (_vocab.TryGetValue(sub, out var sid)) { bestId = sid; bestEnd = end; break; }
                end--;
            }
            if (bestId == -1) { yield return UNKId; yield break; }
            yield return bestId;
            first = false;
            start = bestEnd;
        }
    }
}
