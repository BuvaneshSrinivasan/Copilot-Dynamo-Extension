// =============================================================================
// DynamoCopilot.NodeIndexer — One-time indexing pipeline
// =============================================================================
//
// ── MODE 1: PostgreSQL + Ollama (subscription backend) ───────────────────────
// DO NOT DELETE, SAVED FOR FUTURE USE — feeds the server-side vector search.
//
//   dotnet run -- \
//     --packages    "C:\path\to\downloads" \
//     --connection  "Host=localhost;Database=dynamo;Username=postgres;Password=..." \
//     --ollama-url  "http://localhost:11434"   (optional, default shown)
//
//   Prerequisites: ollama pull nomic-embed-text
//
// ── MODE 2: ONNX + SQLite (BYOK installer bundle) ────────────────────────────
// Generates nodes.db to ship with the extension installer.
//
//   dotnet run -- \
//     --packages  "C:\path\to\downloads" \
//     --sqlite    "C:\output\nodes.db" \
//     --model     "C:\models\all-MiniLM-L6-v2.onnx" \
//     --vocab     "C:\models\vocab.txt"
//
//   Download model from:
//   https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/tree/main/onnx
//
// The tool is safely re-runnable. Already-indexed nodes are skipped.
// =============================================================================

using DynamoCopilot.NodeIndexer.Database;
using DynamoCopilot.NodeIndexer.Embeddings;
using DynamoCopilot.NodeIndexer.Extractors;
using DynamoCopilot.NodeIndexer.Models;

// ── ARGUMENT PARSING ──────────────────────────────────────────────────────────

var packagesDir = GetArg(args, "--packages");
var connString  = GetArg(args, "--connection");   // Mode 1
var ollamaUrl   = GetArg(args, "--ollama-url") ?? "http://localhost:11434";
var sqlitePath  = GetArg(args, "--sqlite");        // Mode 2
var modelPath   = GetArg(args, "--model");
var vocabPath2  = GetArg(args, "--vocab");

bool mode2 = sqlitePath != null;

if (packagesDir == null || (!mode2 && connString == null))
{
    Console.Error.WriteLine("""
        Usage — Mode 1 (PostgreSQL + Ollama):
          dotnet run -- \
            --packages   <path to downloads folder>      \
            --connection "<PostgreSQL connection string>" \
            --ollama-url "http://localhost:11434"         (optional)

        Usage — Mode 2 (SQLite + ONNX, for installer bundle):
          dotnet run -- \
            --packages  <path to downloads folder> \
            --sqlite    <output path for nodes.db>  \
            --model     <path to all-MiniLM-L6-v2.onnx> \
            --vocab     <path to vocab.txt>
        """);
    return 1;
}

if (mode2 && (modelPath == null || vocabPath2 == null))
{
    Console.Error.WriteLine("--sqlite mode requires --model and --vocab.");
    return 1;
}

if (!Directory.Exists(packagesDir))
{
    Console.Error.WriteLine($"Packages directory not found: {packagesDir}");
    return 1;
}

// ── SETUP ─────────────────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct      = cts.Token;
var logPath = Path.Combine(AppContext.BaseDirectory, "indexer-errors.log");

// ── MODE DISPATCH ─────────────────────────────────────────────────────────────

if (mode2)
    return await RunMode2Async(packagesDir!, sqlitePath!, modelPath!, vocabPath2!, ct);

// ── MODE 1: SETUP + HEALTH CHECK ─────────────────────────────────────────────
// DO NOT DELETE, SAVED FOR FUTURE USE

var embedder = new OllamaEmbedder(ollamaUrl);

Console.Write("Checking Ollama... ");
if (!await embedder.HealthCheckAsync(ct))
{
    foreach (var err in embedder.Errors)
        Console.Error.WriteLine(err);
    return 1;
}
Console.WriteLine("OK (nomic-embed-text ready)");

// ── PHASE 1: EXTRACTION (parallel) ───────────────────────────────────────────

var zipFiles    = Directory.GetFiles(packagesDir, "*.zip");
var packageDirs = zipFiles.Length == 0
    ? Directory.GetDirectories(packagesDir)
        .Where(d => File.Exists(Path.Combine(d, "pkg.json")))
        .ToArray()
    : Array.Empty<string>();

bool usingFolders = zipFiles.Length == 0 && packageDirs.Length > 0;
int  totalItems   = usingFolders ? packageDirs.Length : zipFiles.Length;

Console.WriteLine(usingFolders
    ? $"\nFound {totalItems:N0} unpacked package folders in {packagesDir}"
    : $"\nFound {totalItems:N0} zip files in {packagesDir}");
Console.WriteLine("\nPhase 1/3 — Extracting node metadata from packages...");

var allRecords = new System.Collections.Concurrent.ConcurrentBag<NodeRecord>();
var extracted  = 0;

if (usingFolders)
{
    await Parallel.ForEachAsync(
        packageDirs,
        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2, CancellationToken = ct },
        (dir, _) =>
        {
            var records = PackageExtractor.ExtractFromDirectory(dir);
            foreach (var r in records) allRecords.Add(r);

            var count = Interlocked.Increment(ref extracted);
            if (count % 250 == 0 || count == totalItems)
                WriteProgress($"  {count:N0}/{totalItems:N0} packages scanned, {allRecords.Count:N0} nodes found");

            return ValueTask.CompletedTask;
        });
}
else
{
    await Parallel.ForEachAsync(
        zipFiles,
        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2, CancellationToken = ct },
        (zipPath, _) =>
        {
            var records = PackageExtractor.ExtractFromZip(zipPath);
            foreach (var r in records) allRecords.Add(r);

            var count = Interlocked.Increment(ref extracted);
            if (count % 250 == 0 || count == totalItems)
                WriteProgress($"  {count:N0}/{totalItems:N0} packages scanned, {allRecords.Count:N0} nodes found");

            return ValueTask.CompletedTask;
        });
}

Console.WriteLine($"\n  Extraction complete: {allRecords.Count:N0} nodes from {totalItems:N0} packages");

// ── PHASE 2: SKIP ALREADY-INDEXED ────────────────────────────────────────────

Console.WriteLine("\nPhase 2/3 — Checking which nodes are already in the database...");

using var repo = new NodeRepository(connString);

Console.Write("  Ensuring schema exists... ");
await repo.EnsureSchemaAsync(ct);
Console.WriteLine("OK");

var indexedKeys = await repo.LoadIndexedKeysAsync(ct);
var deduped          = allRecords
    .Where(r => !indexedKeys.Contains((r.PackageName, r.Name)))
    .GroupBy(r => (r.PackageName, r.Name))
    .Select(g => g.First())
    .ToList();

Console.WriteLine($"  {indexedKeys.Count:N0} already indexed, {deduped.Count:N0} new nodes to embed");

if (deduped.Count == 0)
{
    Console.WriteLine("\nNothing to do — all nodes are already indexed.");
    return 0;
}

// ── PHASE 3: EMBED + STORE ───────────────────────────────────────────────────

Console.WriteLine($"\nPhase 3/3 — Embedding {deduped.Count:N0} nodes via Ollama + storing in PostgreSQL...");
Console.WriteLine("  (Ctrl+C at any time — progress is saved per batch)\n");

const int DbBatchSize    = 200;  // rows per INSERT statement
const int EmbedBatchSize = 50;   // items per Ollama /api/embed call

var totalEmbedded  = 0;
var totalFailed    = 0;
var dbBuffer       = new List<(NodeRecord Record, float[] Embedding)>();
var lastErrorCount = 0;

for (int i = 0; i < deduped.Count; i += EmbedBatchSize)
{
    ct.ThrowIfCancellationRequested();

    var batch = deduped.Skip(i).Take(EmbedBatchSize).ToList();
    float[]?[] embeddings;

    try
    {
        embeddings = await embedder.EmbedBatchAsync(batch, ct);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        embedder.Errors.Add($"[batch {i}] Unhandled: {ex.GetType().Name}: {ex.Message}");
        totalFailed += batch.Count;
        continue;
    }

    for (int j = 0; j < batch.Count; j++)
    {
        if (embeddings[j] != null) dbBuffer.Add((batch[j], embeddings[j]!));
        else totalFailed++;
    }

    bool isLast = i + EmbedBatchSize >= deduped.Count;
    if (dbBuffer.Count >= DbBatchSize || (isLast && dbBuffer.Count > 0))
    {
        try
        {
            await repo.UpsertAsync(dbBuffer, ct);
            totalEmbedded += dbBuffer.Count;
            dbBuffer.Clear();
        }
        catch (Exception ex) { embedder.Errors.Add($"[DB write] {ex.Message}"); }
    }

    if (embedder.Errors.Count > lastErrorCount)
    {
        var newErrors = embedder.Errors.Skip(lastErrorCount).ToList();
        Console.WriteLine();
        foreach (var err in newErrors) Console.WriteLine($"  ERROR: {err}");
        await File.AppendAllLinesAsync(logPath, newErrors, ct);
        lastErrorCount = embedder.Errors.Count;
    }

    WriteProgress($"  {totalEmbedded:N0}/{deduped.Count:N0} stored"
        + (totalFailed > 0 ? $"  ({totalFailed:N0} failed)" : ""));
}

if (embedder.Errors.Count > 0)
{
    await File.WriteAllLinesAsync(logPath, embedder.Errors);
    Console.WriteLine($"\n  Full error log: {logPath}");
}

// ── SUMMARY ───────────────────────────────────────────────────────────────────

Console.WriteLine($"\n\nDone.");
Console.WriteLine($"  Nodes embedded and stored : {totalEmbedded:N0}");
Console.WriteLine($"  Nodes failed (skipped)    : {totalFailed:N0}");
Console.WriteLine($"  Total in database         : {indexedKeys.Count + totalEmbedded:N0}");

return 0;

// ── HELPERS ───────────────────────────────────────────────────────────────────

static string? GetArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static void WriteProgress(string message) =>
    Console.Write($"\r{message.PadRight(80)}");

// ── MODE 2: ONNX + SQLite ─────────────────────────────────────────────────────

static async Task<int> RunMode2Async(
    string packagesDir, string sqlitePath, string modelPath, string vocabPath,
    CancellationToken ct)
{
    Console.WriteLine("Mode 2: ONNX + SQLite export");

    if (!File.Exists(modelPath)) { Console.Error.WriteLine($"Model not found: {modelPath}"); return 1; }
    if (!File.Exists(vocabPath)) { Console.Error.WriteLine($"Vocab not found: {vocabPath}");  return 1; }

    // ── Phase 1: Extract ──────────────────────────────────────────────────────

    Console.WriteLine("\nPhase 1/3 — Extracting node metadata...");

    var zipFiles    = Directory.GetFiles(packagesDir, "*.zip");
    var packageDirs = zipFiles.Length == 0
        ? Directory.GetDirectories(packagesDir)
            .Where(d => File.Exists(Path.Combine(d, "pkg.json"))).ToArray()
        : Array.Empty<string>();
    bool usingFolders = zipFiles.Length == 0 && packageDirs.Length > 0;
    var  sources      = usingFolders ? packageDirs : zipFiles;

    var allRecords = new System.Collections.Concurrent.ConcurrentBag<NodeRecord>();
    var extracted  = 0;

    await Parallel.ForEachAsync(sources,
        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2, CancellationToken = ct },
        (path, _) =>
        {
            var records = usingFolders
                ? PackageExtractor.ExtractFromDirectory(path)
                : PackageExtractor.ExtractFromZip(path);
            foreach (var r in records) allRecords.Add(r);
            var c = Interlocked.Increment(ref extracted);
            if (c % 250 == 0) WriteProgress($"  {c:N0}/{sources.Length:N0} scanned, {allRecords.Count:N0} nodes");
            return ValueTask.CompletedTask;
        });

    Console.WriteLine($"\n  {allRecords.Count:N0} nodes extracted");

    // ── Phase 2: Deduplicate against existing SQLite ──────────────────────────

    Console.WriteLine("\nPhase 2/3 — Checking for already-indexed nodes...");
    using var exporter    = new SqliteExporter(sqlitePath);
    var       indexedKeys = exporter.LoadIndexedKeys();
    var deduped = allRecords
        .Where(r => !indexedKeys.Contains((r.PackageName, r.Name)))
        .GroupBy(r => (r.PackageName, r.Name))
        .Select(g => g.First())
        .ToList();

    Console.WriteLine($"  {indexedKeys.Count:N0} already in DB, {deduped.Count:N0} new nodes to embed");
    if (deduped.Count == 0) { Console.WriteLine("\nNothing to do."); return 0; }

    // ── Phase 3: Embed + store ────────────────────────────────────────────────

    Console.WriteLine($"\nPhase 3/3 — Embedding {deduped.Count:N0} nodes with ONNX model...\n");

    using var onnx = new OnnxEmbedder(modelPath, vocabPath);

    const int BatchSize    = 50;
    var       totalStored  = 0;
    var       totalFailed2 = 0;

    for (int i = 0; i < deduped.Count; i += BatchSize)
    {
        ct.ThrowIfCancellationRequested();

        var batch      = deduped.Skip(i).Take(BatchSize).ToList();
        var embeddings = await onnx.EmbedBatchAsync(batch, ct);

        var toStore = new List<(NodeRecord, float[])>();
        for (int j = 0; j < batch.Count; j++)
        {
            if (embeddings[j] != null) toStore.Add((batch[j], embeddings[j]!));
            else totalFailed2++;
        }

        if (toStore.Count > 0)
        {
            await exporter.UpsertAsync(toStore, ct);
            totalStored += toStore.Count;
        }

        WriteProgress($"  {totalStored:N0}/{deduped.Count:N0} stored"
            + (totalFailed2 > 0 ? $"  ({totalFailed2:N0} failed)" : ""));
    }

    Console.WriteLine($"\n\nDone.");
    Console.WriteLine($"  Nodes stored : {totalStored:N0}");
    Console.WriteLine($"  Failed       : {totalFailed2:N0}");
    Console.WriteLine($"  Output file  : {sqlitePath}");
    return 0;
}
