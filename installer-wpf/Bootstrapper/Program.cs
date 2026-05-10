using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DynamoCopilot.Bootstrapper
{
    static class Program
    {
        // Official Microsoft redirect — always points to latest .NET 8 Desktop Runtime (x64).
        const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!IsDotNet8Installed())
            {
                var answer = MessageBox.Show(
                    ".NET 8 Desktop Runtime is required to run DynamoCopilot Setup.\n\n" +
                    "Click Yes to download and install it automatically (~55 MB), or No to cancel.",
                    "DynamoCopilot Setup — Prerequisites",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (answer != DialogResult.Yes) return;

                bool installOk = false;
                var form = new ProgressForm();
                form.Load += async (s, e) =>
                {
                    try
                    {
                        await DownloadAndInstallRuntimeAsync(form);
                        installOk = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "Failed to install .NET 8 Desktop Runtime:\n\n" + ex.Message +
                            "\n\nPlease install it manually from:\nhttps://dotnet.microsoft.com/download/dotnet/8.0",
                            "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);

                if (!installOk) return;
            }

            LaunchInnerInstaller();
        }

        static bool IsDotNet8Installed()
        {
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");

            if (!Directory.Exists(runtimeDir)) return false;

            return Directory.GetDirectories(runtimeDir, "8.*").Length > 0;
        }

        static async Task DownloadAndInstallRuntimeAsync(ProgressForm form)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), "dotnet8-desktop-runtime-setup.exe");

            try
            {
                form.SetStatus("Downloading .NET 8 Desktop Runtime...");
                form.SetProgress(0);

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var response = await client.GetAsync(RuntimeUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;

                    using (var src = await response.Content.ReadAsStreamAsync())
                    using (var dst = File.Create(tempFile))
                    {
                        var buffer = new byte[81920];
                        long downloaded = 0;
                        int read;

                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                            downloaded += read;
                            if (total > 0)
                                form.SetProgress((int)(downloaded * 100L / total));
                        }
                    }
                }

                form.SetStatus("Installing .NET 8 Desktop Runtime — please wait...");
                form.SetProgress(-1);

                // Already running elevated (requireAdministrator manifest), so no runas needed.
                var psi = new ProcessStartInfo
                {
                    FileName = tempFile,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi)!;
                await Task.Run(() => proc.WaitForExit());

                // 3010 = success, reboot required — acceptable for our purposes.
                if (proc.ExitCode != 0 && proc.ExitCode != 3010)
                    throw new Exception($"Runtime installer exited with code {proc.ExitCode}.");
            }
            finally
            {
                if (File.Exists(tempFile))
                    try { File.Delete(tempFile); } catch { }
            }
        }

        static void LaunchInnerInstaller()
        {
            // Write the embedded WPF installer to a unique temp path and launch it.
            // The temp file stays on disk while the inner installer runs; Windows cleans temp periodically.
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"DynamoCopilot-inner-{Guid.NewGuid():N}.exe");

            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("inner-installer.exe"))
                {
                    if (stream == null)
                    {
                        MessageBox.Show(
                            "Installer payload is missing. Please re-download the setup file.",
                            "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    using (var file = File.Create(tempPath))
                        stream.CopyTo(file);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true   // inherit elevation; no second UAC prompt
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to launch installer:\n\n" + ex.Message,
                    "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (File.Exists(tempPath))
                    try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
