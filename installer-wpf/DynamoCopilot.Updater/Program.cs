using System;
using System.Diagnostics;
using System.IO;

namespace DynamoCopilot.Updater
{
    // Tiny helper launched by the extension after a staged DLL update is downloaded.
    // Waits for the Revit host process to fully exit, then copies the staged files
    // from %AppData%\DynamoCopilot\update\ over the live extension files.
    //
    // Usage: DynamoCopilot.Updater.exe --apply-update <pid> <newVersion>
    static class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2 || args[0] != "--apply-update")
            {
                Log("Usage: DynamoCopilot.Updater.exe --apply-update <pid> <newVersion>");
                return;
            }

            if (!int.TryParse(args[1], out var pid))
            {
                Log("Invalid process ID: " + args[1]);
                return;
            }

            var newVersion = args.Length >= 3 ? args[2] : "unknown";

            var appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var destBase   = Path.Combine(appData, "DynamoCopilot");
            var stagingDir = Path.Combine(destBase, "update");

            if (!Directory.Exists(stagingDir))
            {
                Log("No staged update found — nothing to do.");
                return;
            }

            // Read the currently installed version before we overwrite anything.
            var currentVersion = GetInstalledVersion(destBase);

            Log("Staged: v" + newVersion
                + " | Installed: " + (currentVersion ?? "unknown")
                + " | Waiting for Revit PID " + pid + " to exit…");

            try
            {
                var target = Process.GetProcessById(pid);
                target.WaitForExit();
            }
            catch (ArgumentException)
            {
                // Process already exited — proceed with the update.
                Log("Revit PID " + pid + " already exited.");
            }
            catch (Exception ex)
            {
                Log("Warning: could not wait for process: " + ex.Message);
            }

            Log("Revit exited. Installing v" + newVersion + "…");
            ApplyUpdate(stagingDir, destBase, currentVersion ?? "?", newVersion);
        }

        static void ApplyUpdate(string stagingDir, string destBase, string fromVersion, string toVersion)
        {
            int success = 0;
            int skipped = 0;

            try
            {
                foreach (var srcFile in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(srcFile);

                    // Skip updating ourselves — the running Updater.exe is locked by the OS.
                    if (string.Equals(fileName, "DynamoCopilot.Updater.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    var relPath  = srcFile.Substring(stagingDir.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    var destFile = Path.Combine(destBase, relPath);
                    var destDir  = Path.GetDirectoryName(destFile)!;

                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    try
                    {
                        File.Copy(srcFile, destFile, overwrite: true);
                        success++;
                    }
                    catch (Exception ex)
                    {
                        Log("Skipped (locked?): " + relPath + " — " + ex.Message);
                        skipped++;
                    }
                }

                Directory.Delete(stagingDir, recursive: true);
                Log("Done. v" + fromVersion + " → v" + toVersion
                    + " (" + success + " files updated, " + skipped + " skipped).");
            }
            catch (Exception ex)
            {
                Log("Fatal error during update: " + ex.Message);
            }
        }

        static string? GetInstalledVersion(string destBase)
        {
            // Try net8.0-windows first (Revit 2025+), fall back to net48.
            foreach (var tfm in new[] { "net8.0-windows", "net48" })
            {
                var dll = Path.Combine(destBase, tfm, "DynamoCopilot.Extension.dll");
                if (!File.Exists(dll)) continue;
                try
                {
                    return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? null;
                }
                catch { }
            }
            return null;
        }

        static void Log(string message)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DynamoCopilot", "updater.log");

                File.AppendAllText(logPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
