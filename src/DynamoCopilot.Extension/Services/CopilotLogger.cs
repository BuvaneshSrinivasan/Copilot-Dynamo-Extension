using System;
using System.IO;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Appends timestamped entries to %TEMP%\DynamoCopilot.log.
    /// Never throws — logging failures are swallowed silently.
    /// </summary>
    internal static class CopilotLogger
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "DynamoCopilot.log");

        public static void Log(string context, Exception ex)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                           $"  Stack: {ex.StackTrace}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }
    }
}
