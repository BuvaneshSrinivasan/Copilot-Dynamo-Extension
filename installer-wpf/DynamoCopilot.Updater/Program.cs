using System;
using System.Diagnostics;
using System.IO;

namespace DynamoCopilot.Updater
{
    // Tiny helper launched by the extension after a staged DLL update is downloaded.
    // Waits for the host process (Revit/Dynamo) to exit, then copies the staged files
    // from %AppData%\DynamoCopilot\update\ over the live extension files.
    //
    // Usage: DynamoCopilot.Updater.exe --apply-update <pid>
    static class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2 || args[0] != "--apply-update")
            {
                Log("Usage: DynamoCopilot.Updater.exe --apply-update <pid>");
                return;
            }

            if (!int.TryParse(args[1], out var pid))
            {
                Log("Invalid process ID: " + args[1]);
                return;
            }

            var appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var destBase   = Path.Combine(appData, "DynamoCopilot");
            var stagingDir = Path.Combine(destBase, "update");

            if (!Directory.Exists(stagingDir))
            {
                Log("No staged update found — nothing to do.");
                return;
            }

            Log("Waiting for process " + pid + " to exit…");

            try
            {
                var target = Process.GetProcessById(pid);
                target.WaitForExit();
            }
            catch (ArgumentException)
            {
                // Process already exited — proceed with the update.
            }
            catch (Exception ex)
            {
                Log("Warning: could not wait for process: " + ex.Message);
            }

            Log("Host process exited. Applying update…");
            ApplyUpdate(stagingDir, destBase);
        }

        static void ApplyUpdate(string stagingDir, string destBase)
        {
            int success = 0;
            int skipped = 0;

            try
            {
                foreach (var srcFile in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(srcFile);

                    // Skip updating ourselves — the running Updater.exe is locked by the OS.
                    // It will be updated the next time the user runs the full installer.
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
                        Log("Updated: " + relPath);
                    }
                    catch (Exception ex)
                    {
                        Log("Skipped (locked?): " + relPath + " — " + ex.Message);
                        skipped++;
                    }
                }

                Directory.Delete(stagingDir, recursive: true);
                Log("Done. " + success + " files updated, " + skipped + " skipped.");
            }
            catch (Exception ex)
            {
                Log("Fatal error during update: " + ex.Message);
            }
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
