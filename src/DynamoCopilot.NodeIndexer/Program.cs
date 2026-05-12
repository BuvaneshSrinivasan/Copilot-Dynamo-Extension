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

using DynamoCopilot.NodeIndexer;
using DynamoCopilot.NodeIndexer.Database;
using DynamoCopilot.NodeIndexer.Embeddings;
using DynamoCopilot.NodeIndexer.Extractors;
using DynamoCopilot.NodeIndexer.Models;

// ── ARGUMENT PARSING ──────────────────────────────────────────────────────────

var packagesDir = GetArg(args, "--packages");
var connString  = GetArg(args, "--connection");   // Mode 1
var ollamaUrl   = GetArg(args, "--ollama-url") ?? "http://localhost:11434";
var sqlitePath  = GetArg(args, "--sqlite");        // Mode 2 / Update
var modelPath   = GetArg(args, "--model");
var vocabPath2  = GetArg(args, "--vocab");
bool updateMode = args.Contains("--update");
bool fullRebuild = args.Contains("--full");

bool mode2 = sqlitePath != null && !updateMode;

// ── SETUP ─────────────────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// ── UPDATE MODE: incremental download from Dynamo Package Manager ────────────

if (updateMode)
{
    if (sqlitePath == null) { Console.Error.WriteLine("--update requires --sqlite <path to existing nodes.db>"); return 1; }
    if (modelPath  == null) { Console.Error.WriteLine("--update requires --model");  return 1; }
    if (vocabPath2 == null) { Console.Error.WriteLine("--update requires --vocab");  return 1; }
    if (!File.Exists(sqlitePath)) { Console.Error.WriteLine($"nodes.db not found: {sqlitePath}"); return 1; }
    if (!File.Exists(modelPath))  { Console.Error.WriteLine($"Model not found: {modelPath}");     return 1; }
    if (!File.Exists(vocabPath2)) { Console.Error.WriteLine($"Vocab not found: {vocabPath2}");    return 1; }
    return await RunUpdateAsync(sqlitePath, modelPath, vocabPath2, fullRebuild, ct);
}

if (packagesDir == null || (!mode2 && connString == null))
{
    Console.Error.WriteLine("""
        Usage — Update mode (incremental sync from Dynamo Package Manager):
          dotnet run -- \
            --update \
            --sqlite  <path to existing nodes.db>        \
            --model   <path to all-MiniLM-L6-v2.onnx>   \
            --vocab   <path to vocab.txt>

        Usage — Full rebuild (re-download all packages, clear and rebuild nodes.db):
          dotnet run -- \
            --update --full \
            --sqlite  <path to nodes.db>                 \
            --model   <path to all-MiniLM-L6-v2.onnx>   \
            --vocab   <path to vocab.txt>

        Usage — Mode 1 (PostgreSQL + Ollama):
          dotnet run -- \
            --packages   <path to downloads folder>      \
            --connection "<PostgreSQL connection string>" \
            --ollama-url "http://localhost:11434"         (optional)

        Usage — Mode 2 (SQLite + ONNX, full rebuild for installer bundle):
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

// ── UPDATE MODE: incremental sync from Dynamo Package Manager ─────────────────

static async Task<int> RunUpdateAsync(
    string sqlitePath, string modelPath, string vocabPath, bool fullRebuild, CancellationToken ct)
{
    Console.WriteLine(fullRebuild
        ? "Full rebuild: downloading all packages from Dynamo Package Manager"
        : "Update mode: incremental sync from Dynamo Package Manager");

    using var exporter  = new SqliteExporter(sqlitePath);

    DateTime since;
    if (fullRebuild)
    {
        Console.WriteLine("Clearing existing nodes...");
        exporter.ClearAllNodes();
        since = DateTime.MinValue;
    }
    else
    {
        var lastBuilt = exporter.GetLastBuiltAt();
        since = lastBuilt ?? DateTime.UtcNow.AddYears(-1);
        Console.WriteLine(lastBuilt.HasValue
            ? $"Last build: {lastBuilt.Value:yyyy-MM-dd HH:mm:ss} UTC"
            : "Last build: never — using 1 year ago as cutoff");
    }

    // ── Phase 1: Fetch updated package list ──────────────────────────────────

    Console.WriteLine(fullRebuild
        ? "\nPhase 1/3 — Fetching all packages from Dynamo Package Manager..."
        : $"\nPhase 1/3 — Fetching packages updated since {since:yyyy-MM-dd}...");
    using var client  = new PackageManagerClient();
    List<PackageInfo> updated;
    try
    {
        updated = await client.GetPackagesUpdatedSinceAsync(since, ct);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to fetch package list: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"  Found {updated.Count:N0} packages to update");
    if (updated.Count == 0)
    {
        Console.WriteLine("\nDB is already up to date.");
        exporter.SetLastBuiltAt(DateTime.UtcNow);
        return 0;
    }

    // ── Phase 2: Download + extract ──────────────────────────────────────────

    Console.WriteLine($"\nPhase 2/3 — Downloading and extracting {updated.Count:N0} packages...");

    var tempDir = Path.Combine(Path.GetTempPath(), $"DynamoCopilot_Update_{DateTime.UtcNow.Ticks}");
    Directory.CreateDirectory(tempDir);

    var allRecords   = new List<NodeRecord>();
    var downloaded   = 0;
    var downloadFail = 0;

    try
    {
        foreach (var pkg in updated)
        {
            ct.ThrowIfCancellationRequested();
            WriteProgress($"  [{downloaded + downloadFail + 1}/{updated.Count}] {pkg.Name}");
            try
            {
                var zipPath = await client.DownloadPackageAsync(pkg, tempDir, ct);
                var records = PackageExtractor.ExtractFromZip(zipPath);

                exporter.DeletePackageNodes(pkg.Name);
                allRecords.AddRange(records);
                downloaded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  WARNING: {pkg.Name} — {ex.Message}");
                downloadFail++;
            }
        }
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    Console.WriteLine($"\n  {downloaded:N0} downloaded, {downloadFail:N0} failed, {allRecords.Count:N0} nodes extracted");

    if (allRecords.Count == 0)
    {
        Console.WriteLine("\nNo nodes to embed.");
        if (downloaded > 0) exporter.SetLastBuiltAt(DateTime.UtcNow);
        return 0;
    }

    var deduped = allRecords
        .GroupBy(r => (r.PackageName, r.Name))
        .Select(g => g.First())
        .ToList();

    // ── Phase 3: Embed + store ────────────────────────────────────────────────

    Console.WriteLine($"\nPhase 3/3 — Embedding {deduped.Count:N0} nodes with ONNX model...\n");

    if (!File.Exists(modelPath)) { Console.Error.WriteLine($"Model not found: {modelPath}"); return 1; }
    if (!File.Exists(vocabPath)) { Console.Error.WriteLine($"Vocab not found: {vocabPath}");  return 1; }

    using var onnx        = new OnnxEmbedder(modelPath, vocabPath);
    const int BatchSize   = 50;
    var       totalStored = 0;
    var       totalFailed = 0;

    for (int i = 0; i < deduped.Count; i += BatchSize)
    {
        ct.ThrowIfCancellationRequested();

        var batch      = deduped.Skip(i).Take(BatchSize).ToList();
        var embeddings = await onnx.EmbedBatchAsync(batch, ct);

        var toStore = new List<(NodeRecord, float[])>();
        for (int j = 0; j < batch.Count; j++)
        {
            if (embeddings[j] != null) toStore.Add((batch[j], embeddings[j]!));
            else totalFailed++;
        }

        if (toStore.Count > 0)
        {
            await exporter.UpsertAsync(toStore, ct);
            totalStored += toStore.Count;
        }

        WriteProgress($"  {totalStored:N0}/{deduped.Count:N0} stored"
            + (totalFailed > 0 ? $"  ({totalFailed:N0} failed)" : ""));
    }

    exporter.SetLastBuiltAt(DateTime.UtcNow);

    Console.WriteLine($"\n\nDone.");
    Console.WriteLine($"  Packages updated : {downloaded:N0}");
    Console.WriteLine($"  Nodes stored     : {totalStored:N0}");
    Console.WriteLine($"  Nodes failed     : {totalFailed:N0}");
    Console.WriteLine($"  DB file          : {sqlitePath}");
    return 0;
}
