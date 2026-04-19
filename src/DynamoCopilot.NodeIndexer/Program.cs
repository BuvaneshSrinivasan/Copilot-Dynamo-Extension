// =============================================================================
// DynamoCopilot.NodeIndexer — One-time indexing pipeline
// =============================================================================
// Usage:
//   dotnet run -- \
//     --packages    "C:\path\to\downloads" \
//     --connection  "Host=localhost;Database=dynamo;Username=postgres;Password=..." \
//     --ollama-url  "http://localhost:11434"   (optional, this is the default)
//
// Prerequisites:
//   1. Install Ollama: https://ollama.com
//   2. Pull the embedding model: ollama pull nomic-embed-text
//   3. Run: dotnet run -- --packages ... --connection ...
//
// The tool is safely re-runnable. Already-indexed nodes are skipped.
// =============================================================================

using DynamoCopilot.NodeIndexer.Database;
using DynamoCopilot.NodeIndexer.Embeddings;
using DynamoCopilot.NodeIndexer.Extractors;
using DynamoCopilot.NodeIndexer.Models;

// ── ARGUMENT PARSING ──────────────────────────────────────────────────────────

var packagesDir = GetArg(args, "--packages");
var connString  = GetArg(args, "--connection");
var ollamaUrl   = GetArg(args, "--ollama-url") ?? "http://localhost:11434";

if (packagesDir == null || connString == null)
{
    Console.Error.WriteLine("""
        Usage:
          dotnet run -- \
            --packages   <path to zip downloads folder>   \
            --connection "<PostgreSQL connection string>"  \
            --ollama-url "http://localhost:11434"          (optional)

        Prerequisites:
          ollama pull nomic-embed-text
        """);
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

var ct       = cts.Token;
var embedder = new OllamaEmbedder(ollamaUrl);
var logPath  = Path.Combine(AppContext.BaseDirectory, "indexer-errors.log");

// ── HEALTH CHECK ──────────────────────────────────────────────────────────────

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
