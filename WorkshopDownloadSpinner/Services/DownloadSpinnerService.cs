using SDG.Provider;
using SDG.Unturned;
using Steamworks;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using WorkshopDownloadSpinner.Helpers;

namespace WorkshopDownloadSpinner.Services
{
    /// <summary>
    /// Harmony-free replacement for the original coroutine service.
    /// One dedicated background thread watches DedicatedUGC.currentDownload (via reflection, read-only)
    /// and renders the console progress bar from there, so the game's main thread is never blocked
    /// by console I/O. Steamworks query calls are only made while an item is actually downloading.
    /// </summary>
    public class DownloadSpinnerService
    {
        private const float DELAY_SECONDS = 1.5f;
        private const int PROGRESS_BAR_LENGTH = 16;
        private const float UPDATE_ESTIMATE_RATE_SECONDS = 2.5f;
        private const int RENDER_INTERVAL_MS = 100;
        private const int IDLE_POLL_INTERVAL_MS = 200;
        private const int STEAM_MEMORY_RELEASE_INTERVAL_MS = 1000;
        private const int SHUTDOWN_JOIN_TIMEOUT_MS = 1000;

        private const string DownloadingTextKey = "DownloadingText";

        private static readonly char[] SpinnerChars = [ '-', '\\', '|', '/' ];

        private const char ProgressEmptyChar = ' ';
        private const char ProgressFilledChar = '#';

        /// <summary>
        /// Private static field of DedicatedUGC, verified stable in Unturned 3.26.x.
        /// Only ever written by the game on its main thread; our reflection read is safe.
        /// </summary>
        private static readonly FieldInfo? CurrentDownloadField =
            typeof(DedicatedUGC).GetField("currentDownload", BindingFlags.NonPublic | BindingFlags.Static);

        public bool CanWatchDedicatedUGC => CurrentDownloadField != null;

        private readonly Local localization;

        private Thread? watcherThread;
        private volatile bool stopRequested;

        // Session state below is only ever touched by the watcher thread.
        private readonly Stopwatch sessionStopwatch = new Stopwatch();
        private int spinnerStep;
        private double lastEstimateSeconds;
        private ulong lastDownloadedBytes;
        private float bytesPerSecond;

        public DownloadSpinnerService(Local localization)
        {
            this.localization = localization;
        }

        public void StartWatching()
        {
            stopRequested = false;

            watcherThread = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = "WorkshopDownloadSpinner"
            };
            watcherThread.Start();
        }

        /// <summary>
        /// Equivalent of the original OnFinishedDownloadingItems patch: stop any running spinner
        /// and wind the watcher down. Safe to call from the game's main thread.
        /// </summary>
        public void NotifyAllInstalled()
        {
            stopRequested = true;
        }

        public void Shutdown()
        {
            stopRequested = true;

            Thread? thread = watcherThread;
            if (thread != null && thread.IsAlive && Thread.CurrentThread != thread)
            {
                thread.Join(SHUTDOWN_JOIN_TIMEOUT_MS);
            }
            watcherThread = null;

            // Defensive: the watcher finalizes its own session on stop, but a join timeout
            // (e.g. a stalled terminal) could leave the cursor hidden or input discarded.
            // No newline here — the watcher prints it when it unwinds, and blocking console
            // writes on the main thread during shutdown must be avoided.
            SetConsoleInputEnabled(true);
        }

        private void WatchLoop()
        {
            ulong lastSeenDownload = 0;
            long lastSteamMemoryRelease = 0;

            while (!stopRequested && !Provider.isApplicationQuitting)
            {
                try
                {
                    long now = Environment.TickCount;
                    if (now - lastSteamMemoryRelease >= STEAM_MEMORY_RELEASE_INTERVAL_MS)
                    {
                        lastSteamMemoryRelease = now;
                        TryReleaseSteamThreadMemory();
                    }

                    PublishedFileId_t currentDownload = ReadCurrentDownload();
                    // Triggering on a change of currentDownload mirrors the original installNextItem
                    // postfix, which restarted the spinner for every newly dequeued workshop item.
                    if (currentDownload != PublishedFileId_t.Invalid && currentDownload.m_PublishedFileId != lastSeenDownload)
                    {
                        lastSeenDownload = currentDownload.m_PublishedFileId;
                        RunDownloadSession(currentDownload);
                    }
                }
                catch (Exception)
                {
                    // The watcher must never die from an unexpected exception; retry after the idle delay.
                }

                Thread.Sleep(IDLE_POLL_INTERVAL_MS);
            }
        }

        private void RunDownloadSession(PublishedFileId_t item)
        {
            sessionStopwatch.Restart();
            lastEstimateSeconds = 0.0;
            lastDownloadedBytes = 0;
            bytesPerSecond = 0f;
            spinnerStep = 0;

            // Whatever happens below (exceptions included), the console must be left usable.
            try
            {
                // Seed the estimate like the original coroutine did right before its delay.
                if (TryGetDownloadInfo(item, out ulong seededDownloaded, out ulong seededTotal))
                {
                    UpdateEstimate(seededDownloaded, seededTotal);
                }
                else
                {
                    UpdateEstimate(0, 0);
                }

                // Prevent appearing for very fast downloads.
                long delayTicks = (long)(DELAY_SECONDS * TimeSpan.TicksPerSecond);
                while (sessionStopwatch.ElapsedTicks < delayTicks && !stopRequested && !Provider.isApplicationQuitting)
                {
                    Thread.Sleep(RENDER_INTERVAL_MS);
                }

                if (stopRequested || Provider.isApplicationQuitting)
                {
                    // Mirrors the original external StopSpinner: a single trailing newline.
                    return;
                }

                string downloadMessage = BuildDownloadMessage(item);

                Console.WriteLine();
                SetConsoleInputEnabled(false);

                while (!stopRequested && !Provider.isApplicationQuitting)
                {
                    if (!TryGetDownloadInfo(item, out ulong bytesDownloaded, out ulong bytesTotal))
                    {
                        break;
                    }

                    UpdateEstimate(bytesDownloaded, bytesTotal);
                    RenderDownloadLine(downloadMessage, bytesDownloaded, bytesTotal);

                    Thread.Sleep(RENDER_INTERVAL_MS);
                }
            }
            finally
            {
                EndSessionVisuals();
            }
        }

        private string BuildDownloadMessage(PublishedFileId_t item)
        {
            string title = GetItemTitle(item);

            string template = localization.read(DownloadingTextKey);
            if (string.IsNullOrEmpty(template))
            {
                // Identical to the original message, used when the translation files are missing.
                return $"Downloading \"{title}\" ({item.m_PublishedFileId})";
            }

            return string.Format(template, title, item.m_PublishedFileId);
        }

        private static string GetItemTitle(PublishedFileId_t item)
        {
            try
            {
                // Safe to read here: the details dictionary is filled during the query phase,
                // which always completes before the first download starts.
                TempSteamworksWorkshop.getCachedDetails(item, out CachedUGCDetails cachedDetails);
                return cachedDetails.GetTitle();
            }
            catch (Exception)
            {
                return item.m_PublishedFileId.ToString();
            }
        }

        private static bool TryGetDownloadInfo(PublishedFileId_t item, out ulong bytesDownloaded, out ulong bytesTotal)
        {
            bytesDownloaded = 0;
            bytesTotal = 0;
            try
            {
                return SteamGameServerUGC.GetItemDownloadInfo(item, out bytesDownloaded, out bytesTotal);
            }
            catch (Exception)
            {
                // Steam GameServer API not initialized yet or already shut down.
                return false;
            }
        }

        private static void TryReleaseSteamThreadMemory()
        {
            try
            {
                // Query calls allocate thread-local memory; RunCallbacks never pumps on our thread.
                GameServer.ReleaseCurrentThreadMemory();
            }
            catch (Exception)
            {
                // Steam GameServer API not initialized yet or already shut down — retry later.
            }
        }

        private static PublishedFileId_t ReadCurrentDownload()
        {
            if (CurrentDownloadField == null)
            {
                return PublishedFileId_t.Invalid;
            }

            return CurrentDownloadField.GetValue(null) is PublishedFileId_t value
                ? value
                : PublishedFileId_t.Invalid;
        }

        private char NextSpinnerChar()
        {
            spinnerStep++;
            if (spinnerStep >= SpinnerChars.Length) spinnerStep = 0;
            return SpinnerChars[spinnerStep];
        }

        private static string DownloadProgressBar(ulong bytesDownloaded, ulong bytesTotal)
        {
            float progress = bytesTotal > 0 ? (float)bytesDownloaded / (float)bytesTotal : 0f;
            if (progress < 0f) progress = 0f;
            if (progress > 1f) progress = 1f;

            int progressSteps = Math.Clamp((int)(PROGRESS_BAR_LENGTH * progress), 0, PROGRESS_BAR_LENGTH);

            StringBuilder progressBar = new();
            progressBar.Append('[');
            progressBar.Append(ProgressFilledChar, progressSteps);
            progressBar.Append(ProgressEmptyChar, PROGRESS_BAR_LENGTH - progressSteps);
            progressBar.Append(']');
            progressBar.Append(' ');
            progressBar.Append(progress.ToString("P1"));
            progressBar.Append(' ');
            progressBar.Append('(');
            progressBar.Append(bytesDownloaded);
            progressBar.Append('/');
            progressBar.Append(bytesTotal);
            progressBar.Append(')');

            return progressBar.ToString();
        }

        private void UpdateEstimate(ulong bytesDownloaded, ulong bytesTotal)
        {
            if (bytesDownloaded == bytesTotal)
            {
                bytesPerSecond = 0f;
                return;
            }

            double now = sessionStopwatch.Elapsed.TotalSeconds;
            if (bytesPerSecond != 0f && now - lastEstimateSeconds < UPDATE_ESTIMATE_RATE_SECONDS) return;

            double span = now - lastEstimateSeconds;
            bytesPerSecond = span > 0.0 ? (float)((bytesDownloaded - lastDownloadedBytes) / span) : 0f;
            if (bytesPerSecond < 0f) bytesPerSecond = 0f;

            lastDownloadedBytes = bytesDownloaded;
            lastEstimateSeconds = now;
        }

        private string DownloadEstimate()
        {
            return $"{bytesPerSecond.ToBytesString()}/s";
        }

        private void RenderDownloadLine(string downloadMessage, ulong bytesDownloaded, ulong bytesTotal)
        {
            StringBuilder line = new();
            line.Append(downloadMessage);
            line.Append(' ');
            line.Append(NextSpinnerChar());
            line.Append(' ');
            line.Append(DownloadProgressBar(bytesDownloaded, bytesTotal));
            line.Append(' ');
            line.Append(DownloadEstimate());
            line.Append("       "); // Download Estimate changes quite a bit in size, this is to prevent "0 B/s/sssss"

            try
            {
                if (Console.CursorLeft != 0 && Console.BufferWidth > 0)
                {
                    Console.CursorLeft = 0;
                }
            }
            catch (Exception)
            {
                // Console output is redirected (service / docker / SSH without pty) — cursor APIs unavailable.
            }

            // One Write per refresh keeps the line as atomic as System.Console allows,
            // minimizing interleaving with the game's own console output thread.
            Console.Write(line.ToString());
        }

        private static void EndSessionVisuals()
        {
            Console.WriteLine();
            SetConsoleInputEnabled(true);
        }

        private static void SetConsoleInputEnabled(bool enabled)
        {
            try
            {
                Console.CursorVisible = enabled;
            }
            catch (Exception)
            {
                // Cursor APIs are unavailable when console output is redirected.
            }

            ConsoleHelper.DiscardConsoleInput = !enabled;
        }
    }
}
