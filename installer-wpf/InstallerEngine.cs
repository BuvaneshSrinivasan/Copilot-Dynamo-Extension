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

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "DynamoCopilot-Installer");

        // dist.zip: ~119 MB  →  5–25%
        await DownloadAndExtractDistAsync(http, destBase, progress, ct);

        // nodes.db: ~375 MB  →  27–75%
        await DownloadFileAsync(http,
            $"{ReleaseBase}/nodes.db",
            Path.Combine(destBase, "nodes.db"),
            "Downloading node database",
            startPct: 27, endPct: 75, progress, ct);

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

    private static async Task DownloadAndExtractDistAsync(
        HttpClient http, string destBase,
        IProgress<InstallStep> progress, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "dynamo_dist.zip");
        try
        {
            await DownloadFileAsync(http, $"{ReleaseBase}/dist.zip", tempZip,
                "Downloading extension files", startPct: 5, endPct: 25, progress, ct);

            progress.Report(new("Extracting extension files…", 26));
            await Task.Run(() =>
            {
                // Wipe old DLL folders so removed files don't linger across updates.
                foreach (var tfmDir in new[] { "net48", "net8.0-windows" })
                {
                    var dir = Path.Combine(destBase, tfmDir);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }

                using var zip = ZipFile.OpenRead(tempZip);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    // dist.zip layout: dist/net48/… and dist/net8.0-windows/…
                    // Strip the leading "dist/" so files land in destBase\net48\… etc.
                    var entryPath = entry.FullName.Replace('\\', '/');
                    var relPath   = entryPath.StartsWith("dist/", StringComparison.OrdinalIgnoreCase)
                        ? entryPath["dist/".Length..]
                        : entryPath;

                    var destPath = Path.Combine(destBase, relPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }, ct);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
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
