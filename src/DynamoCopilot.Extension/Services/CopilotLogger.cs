using System;
using System.IO;
using System.Reflection;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Appends timestamped step-tracking entries to
    /// %AppData%\DynamoCopilot\log
    /// Never throws — logging failures are swallowed silently.
    /// </summary>
    internal static class CopilotLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamoCopilot", "log");

        // Written once per process lifetime when the first log call is made.
        private static bool _headerWritten;
        private static readonly object _headerLock = new object();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Log a plain message.</summary>
        public static void Log(string message)
        {
            try
            {
                EnsureHeader();
                Write($"[INFO ] {message}");
            }
            catch { }
        }

        /// <summary>Log a named step with an optional detail value.</summary>
        public static void Log(string step, string detail)
        {
            try
            {
                EnsureHeader();
                Write($"[STEP ] {step}: {detail}");
            }
            catch { }
        }

        /// <summary>Log an exception with context.</summary>
        public static void Log(string context, Exception ex)
        {
            try
            {
                EnsureHeader();
                Write($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
                Write($"        {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "        ")}");
            }
            catch { }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static void Write(string line)
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}";
            File.AppendAllText(LogPath, entry);
        }

        private static void EnsureHeader()
        {
            if (_headerWritten) return;
            lock (_headerLock)
            {
                if (_headerWritten) return;
                // Ensure directory exists (AppData\DynamoCopilot may not exist on first run)
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?.?.?.?";
                var header  = $"{new string('=', 72)}{Environment.NewLine}" +
                              $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DynamoCopilot session start  v{version}{Environment.NewLine}" +
                              $"  LogPath : {LogPath}{Environment.NewLine}" +
                              $"  OS      : {Environment.OSVersion}{Environment.NewLine}" +
                              $"  Runtime : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
                              $"{new string('=', 72)}{Environment.NewLine}";
                File.AppendAllText(LogPath, header);
                _headerWritten = true;
            }
        }
    }
}
