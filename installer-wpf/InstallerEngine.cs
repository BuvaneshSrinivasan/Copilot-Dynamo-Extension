using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamoCopilot.Core.Settings;

namespace DynamoCopilot.Installer;

public record InstallStep(string Status, int Percent);

public class InstallerEngine
{
    private static readonly string ProductionUrl = new DynamoCopilotSettings().ServerUrl;
    private const string LocalServerUrl = "http://localhost:8080";
    private const int    MaxHistory     = 40;
    private const string ReleaseBase    = "https://github.com/BuvaneshSrinivasan/Copilot-Dynamo-Extension/releases/download/v1.0.0";

    private static readonly (string RelPath, string Tfm)[] DynamoPaths =
    [
        (@"\Autodesk\Revit 2026\AddIns\DynamoForRevit\viewExtensions", "net8.0-windows"),
        (@"\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions", "net8.0-windows"),
        (@"\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions", "net48"),
        (@"\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions", "net48"),
        (@"\Autodesk\Revit 2022\AddIns\DynamoForRevit\viewExtensions", "net48"),
        (@"\Dynamo\Dynamo Core\3\viewExtensions",                       "net8.0-windows"),
        (@"\Dynamo\Dynamo Core\2.19\viewExtensions",                    "net48"),
        (@"\Dynamo\Dynamo Core\2.18\viewExtensions",                    "net48"),
    ];

    public async Task RunAsync(IProgress<InstallStep> progress, CancellationToken ct = default)
    {
        var appData   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var destBase  = Path.Combine(appData, "DynamoCopilot");
        var modelsDir = Path.Combine(destBase, "models");

        // DLLs + runtimes: embedded in exe payload  →  3–10%
        await ExtractEmbeddedDistAsync(destBase, progress, ct);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "DynamoCopilot-Installer");

        // nodes.db: ~186 MB  →  12–75%
        await DownloadFileAsync(http,
            $"{ReleaseBase}/nodes.db",
            Path.Combine(destBase, "nodes.db"),
            "Downloading node database",
            startPct: 12, endPct: 75, progress, ct);

        // model.onnx: ~87 MB  →  75–90%
        await DownloadFileAsync(http,
            $"{ReleaseBase}/model.onnx",
            Path.Combine(modelsDir, "model.onnx"),
            "Downloading AI model",
            startPct: 75, endPct: 90, progress, ct);

        // vocab.txt: tiny  →  90–92%
        await DownloadFileAsync(http,
            $"{ReleaseBase}/vocab.txt",
            Path.Combine(modelsDir, "vocab.txt"),
            "Downloading vocabulary",
            startPct: 90, endPct: 92, progress, ct);

        progress.Report(new("Writing settings…", 94));
        await Task.Run(() => WriteSettings(destBase), ct);

        progress.Report(new("Registering with Dynamo…", 97));
        await Task.Run(() => RegisterDynamo(destBase), ct);

        progress.Report(new("Finishing…", 99));
        await Task.Delay(400, ct);

        progress.Report(new("Done", 100));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task ExtractEmbeddedDistAsync(
        string destBase, IProgress<InstallStep> progress, CancellationToken ct)
    {
        progress.Report(new("Extracting extension files…", 3));

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine installer path.");

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(exePath);

            // Last 8 bytes = original exe size (little-endian int64) written by append_payload.ps1
            fs.Seek(-8, SeekOrigin.End);
            Span<byte> trailer = stackalloc byte[8];
            fs.ReadExactly(trailer);
            var exeSize = BitConverter.ToInt64(trailer);
            var zipLen  = fs.Length - exeSize - 8;

            if (zipLen <= 0)
                throw new InvalidDataException("No embedded payload found. Rebuild with build-installer.ps1.");

            // Wipe old DLL folders so stale files don't linger across updates
            foreach (var tfm in new[] { "net48", "net8.0-windows" })
            {
                var dir = Path.Combine(destBase, tfm);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }

            using var sub = new SubStream(fs, exeSize, zipLen);
            using var zip = new ZipArchive(sub, ZipArchiveMode.Read, leaveOpen: true);

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Strip leading "dist/" so files land in destBase\net48\… etc.
                var rel  = entry.FullName.Replace('\\', '/');
                var rel2 = rel.StartsWith("dist/", StringComparison.OrdinalIgnoreCase)
                    ? rel["dist/".Length..]
                    : rel;

                var dest = Path.Combine(destBase, rel2.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }, ct);

        progress.Report(new("Extension files extracted.", 10));
    }

    // Wraps a byte-range of an existing stream so ZipArchive never sees exe bytes
    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long   _start;
        private readonly long   _length;
        private          long   _pos;

        public SubStream(Stream inner, long start, long length)
        {
            _inner  = inner;
            _start  = start;
            _length = length;
            _inner.Seek(start, SeekOrigin.Begin);
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get => _pos;
            set { _inner.Seek(_start + value, SeekOrigin.Begin); _pos = value; }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _pos;
            if (remaining <= 0) return 0;
            count = (int)Math.Min(count, remaining);
            var read = _inner.Read(buffer, offset, count);
            _pos += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End     => _length + offset,
                _                  => throw new ArgumentException("Invalid origin", nameof(origin))
            };
            _inner.Seek(_start + newPos, SeekOrigin.Begin);
            _pos = newPos;
            return _pos;
        }

        public override void SetLength(long value)                       => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private static async Task DownloadFileAsync(
        HttpClient http, string url, string destPath,
        string label, int startPct, int endPct,
        IProgress<InstallStep> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);

        var buffer     = new byte[81920];
        long downloaded = 0;
        int  read;

        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            if (total > 0)
            {
                var pct    = startPct + (int)(downloaded * (endPct - startPct) / total);
                var doneMb = downloaded / 1_048_576.0;
                var totMb  = total      / 1_048_576.0;
                progress.Report(new($"{label} ({doneMb:F0} / {totMb:F0} MB)…", pct));
            }
        }
    }

    private static void WriteSettings(string destBase)
    {
        Directory.CreateDirectory(destBase);
        var path = Path.Combine(destBase, "settings.json");

        int existingMaxHistory  = MaxHistory;
        string existingLocalUrl = LocalServerUrl;

        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("maxHistoryMessages", out var mh)) existingMaxHistory = mh.GetInt32();
                if (root.TryGetProperty("localServerUrl",     out var lu)) existingLocalUrl   = lu.GetString() ?? LocalServerUrl;
            }
            catch { /* corrupt file – use defaults */ }
        }

        var settings = new
        {
            serverUrl          = ProductionUrl,
            maxHistoryMessages = existingMaxHistory,
            useLocalServer     = false,
            localServerUrl     = existingLocalUrl,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RegisterDynamo(string destBase)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var (relPath, tfm) in DynamoPaths)
        {
            var dir = pf + relPath;
            if (!Directory.Exists(dir)) continue;

            var dllPath = Path.Combine(destBase, tfm, "DynamoCopilot.Extension.dll");
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <ViewExtensionDefinition>
                  <AssemblyPath>{dllPath}</AssemblyPath>
                  <TypeName>DynamoCopilot.Extension.DynamoCopilotViewExtension</TypeName>
                </ViewExtensionDefinition>
                """;

            File.WriteAllText(Path.Combine(dir, "DynamoCopilot_ViewExtensionDefinition.xml"), xml);
        }
    }
}
