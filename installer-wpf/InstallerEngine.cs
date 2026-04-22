using System;
using System.IO;
using System.IO.Compression;
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
        var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var destBase = Path.Combine(appData, "DynamoCopilot");

        progress.Report(new("Extracting files…", 20));
        await Task.Run(() => ExtractPayload(destBase), ct);

        progress.Report(new("Writing settings…", 70));
        await Task.Run(() => WriteSettings(destBase), ct);

        progress.Report(new("Registering with Dynamo…", 80));
        await Task.Run(() => RegisterDynamo(destBase), ct);

        progress.Report(new("Finishing…", 95));
        await Task.Delay(400, ct);

        progress.Report(new("Done", 100));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ExtractPayload(string destBase)
    {
        // Layout: [exe bytes][zip bytes][zip_start_offset: int64 LE, 8 bytes]
        // The 8-byte trailer tells us exactly where the zip region begins so we can
        // give ZipArchive a SubStream over that region only — its internal offsets
        // are then correct and it never sees the exe bytes.
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");

        using var exeStream = File.OpenRead(exePath);

        exeStream.Seek(-8, SeekOrigin.End);
        var buf = new byte[8];
        exeStream.ReadExactly(buf);
        var zipStart  = BitConverter.ToInt64(buf);
        var zipLength = exeStream.Length - 8 - zipStart;

        if (zipStart <= 0 || zipLength <= 0)
            throw new InvalidOperationException(
                "Payload not found. This installer may be corrupted — please re-download it.");

        using var zipStream = new SubStream(exeStream, zipStart, zipLength);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries

            // zip layout:   dist/net48/…  →  destBase\net48\…
            //               dist/net8.0-windows/…  →  destBase\net8.0-windows\…
            //               models/…  →  destBase\models\…
            //               nodes.db  →  destBase\nodes.db
            var entryPath = entry.FullName.Replace('\\', '/');
            var relPath = entryPath.StartsWith("dist/", StringComparison.OrdinalIgnoreCase)
                ? entryPath["dist/".Length..]
                : entryPath;

            var destPath = Path.Combine(destBase, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    // Exposes a contiguous sub-region of an existing stream as a seekable read-only stream.
    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long   _start;
        private readonly long   _length;
        private long _pos;

        public SubStream(Stream inner, long start, long length)
        {
            _inner = inner; _start = start; _length = length;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get => _pos;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _pos;
            if (remaining <= 0) return 0;
            count = (int)Math.Min(count, remaining);
            _inner.Seek(_start + _pos, SeekOrigin.Begin);
            var read = _inner.Read(buffer, offset, count);
            _pos += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End     => _length + offset,
                _                  => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void WriteSettings(string destBase)
    {
        Directory.CreateDirectory(destBase);
        var path = Path.Combine(destBase, "settings.json");

        // Preserve user-customised values if the file already exists
        int existingMaxHistory = MaxHistory;
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
