using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WorkshopDownloadSpinner.Diagnostics
{
    /// <summary>
    /// Lightweight diagnostic logger. Inactive unless a "WorkshopDownloadSpinner.diagnostic"
    /// marker file exists in the module directory; the log is written next to it.
    /// Every member is exception-safe and cheap (a null check) when disabled.
    /// </summary>
    internal static class DiagnosticLog
    {
        private const string MarkerFileName = "WorkshopDownloadSpinner.diagnostic";
        private const string LogFileName = "WorkshopDownloadSpinner-diagnostic.log";

        private static readonly object WriteLock = new object();
        private static StreamWriter? writer;

        public static bool Active => writer != null;

        public static string? LogPath { get; private set; }

        /// <summary>
        /// True when the marker file exists, even if the log file itself could not be created.
        /// </summary>
        public static bool MarkerExists(string moduleDirectory)
        {
            try
            {
                return File.Exists(Path.Combine(moduleDirectory, MarkerFileName));
            }
            catch
            {
                return false;
            }
        }

        public static bool TryBegin(string moduleDirectory)
        {
            try
            {
                if (!MarkerExists(moduleDirectory))
                {
                    return false;
                }

                LogPath = Path.Combine(moduleDirectory, LogFileName);
                writer = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
                Write($"=== diagnostic session start ({Environment.Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
                return true;
            }
            catch (Exception exception)
            {
                WriteStartupFailure($"could not create {LogFileName}", exception);
                writer = null;
                LogPath = null;
                return false;
            }
        }

        public static void Write(string message)
        {
            StreamWriter? snapshot = writer;
            if (snapshot == null)
            {
                return;
            }

            try
            {
                lock (WriteLock)
                {
                    snapshot.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [T{Thread.CurrentThread.ManagedThreadId}] {message}");
                }
            }
            catch
            {
                // Never let diagnostics break the module.
            }
        }

        public static void Write(string message, Exception exception)
        {
            Write($"{message} -> {exception.GetType().Name}: {exception.Message}");
            Write(exception.StackTrace ?? "(no stack trace)");
        }

        private static void WriteStartupFailure(string message, Exception exception)
        {
            try
            {
                SDG.Unturned.CommandWindow.LogError($"[WDSP] {message}: {exception.Message}");
            }
            catch
            {
                // Console may not be ready — nothing more we can do.
            }
        }
    }
}
