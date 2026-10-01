using System;
using System.IO;
using Microsoft.VisualStudio.Shell;

namespace SsmsAutoConnect
{
    /// <summary>
    /// Writes to the VS ActivityLog (only persisted when SSMS runs with /log) and to
    /// %AppData%\SsmsAutoConnect\autoconnect.log (always, overwritten on each SSMS start).
    /// Safe to call from any thread.
    /// </summary>
    internal static class Log
    {
        private const string Source = "SsmsAutoConnect";
        private static readonly object FileLock = new object();
        private static bool fileReset;

        public static void Info(string message) => Write(false, message);

        public static void Error(string message, Exception ex = null) =>
            Write(true, ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(bool isError, string message)
        {
            WriteFile((isError ? "ERROR " : "INFO  ") + message);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (isError)
                        ActivityLog.LogError(Source, message);
                    else
                        ActivityLog.LogInformation(Source, message);
                }
                catch
                {
                    // Logging must never take SSMS down.
                }
            });
        }

        private static void WriteFile(string line)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(ConnectionConfig.ConfigDirectory);
                    string path = Path.Combine(ConnectionConfig.ConfigDirectory, "autoconnect.log");
                    string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{System.Threading.Thread.CurrentThread.ManagedThreadId}] {line}{Environment.NewLine}";
                    if (!fileReset)
                    {
                        File.WriteAllText(path, text);
                        fileReset = true;
                    }
                    else
                    {
                        File.AppendAllText(path, text);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
