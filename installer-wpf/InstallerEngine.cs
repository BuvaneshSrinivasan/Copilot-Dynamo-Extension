using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.Installer;

public record InstallStep(string Status, int Percent);

public class InstallerEngine
{
    private const string ProductionUrl  = "https://radiant-determination-production.up.railway.app";
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
        var distDir  = Path.Combine(AppContext.BaseDirectory, "dist");

        progress.Report(new("Copying files (net4.8)…", 10));
        await Task.Run(() => CopyTfm(distDir, destBase, "net48"), ct);

        progress.Report(new("Copying files (net8.0)…", 35));
        await Task.Run(() => CopyTfm(distDir, destBase, "net8.0-windows"), ct);

        progress.Report(new("Writing settings…", 60));
        await Task.Run(() => WriteSettings(destBase), ct);

        progress.Report(new("Registering with Dynamo…", 78));
        await Task.Run(() => RegisterDynamo(destBase), ct);

        progress.Report(new("Finishing…", 95));
        await Task.Delay(400, ct);   // let the bar reach 95% visibly

        progress.Report(new("Done", 100));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CopyTfm(string distDir, string destBase, string tfm)
    {
        var src = Path.Combine(distDir, tfm);
        var dst = Path.Combine(destBase, tfm);
        if (!Directory.Exists(src)) return;

        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel    = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
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
