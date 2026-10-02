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

        /// <summary>
        /// GetItemDownloadInfo can keep returning true with (0, 0) forever after an item finished
        /// (e.g. Steam re-flags it k_EItemStateNeedsUpdate without a download running), so a session
        /// that sees nothing but zeroes for this long is treated as finished regardless.
        /// </summary>
        private const int ALL_ZERO_STALL_TIMEOUT_MS = 10000;

        private const string DownloadingTextKey = "DownloadingText";
        private const string EtaTextKey = "EtaText";

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

                int zeroStallStartTick = 0;
                while (!stopRequested && !Provider.isApplicationQuitting)
                {
                    if (!TryGetDownloadInfo(item, out ulong bytesDownloaded, out ulong bytesTotal))
                    {
                        break;
                    }

                    // The original module force-stopped the spinner from its installDownloadedItem
                    // patch; the state flags are the reflection-free equivalent of that signal.
                    // Without it, GetItemDownloadInfo may keep returning true with (0, 0) forever
                    // once Steam re-flags a finished item as needing an update.
                    if (!IsDownloadActive(item))
                    {
                        break;
                    }

                    if (bytesDownloaded == 0 && bytesTotal == 0)
                    {
                        // Safety net: a real download leaves the (0, 0) state almost immediately.
                        int now = Environment.TickCount;
                        if (zeroStallStartTick == 0)
                        {
                            zeroStallStartTick = now;
                        }
                        else if (now - zeroStallStartTick > ALL_ZERO_STALL_TIMEOUT_MS)
                        {
                            break;
                        }
                    }
                    else
                    {
                        zeroStallStartTick = 0;
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

        /// <summary>
        /// True while Steam reports the item as downloading (or about to download).
        /// Used to end a session the same way the original module's installDownloadedItem patch did.
        /// </summary>
        private static bool IsDownloadActive(PublishedFileId_t item)
        {
            try
            {
                uint state = SteamGameServerUGC.GetItemState(item);
                uint downloadFlags = (uint)EItemState.k_EItemStateDownloading | (uint)EItemState.k_EItemStateDownloadPending;
                return (state & downloadFlags) != 0u;
            }
            catch (Exception)
            {
                // Steam GameServer API unavailable — never end the session on our own signal here;
                // the next GetItemDownloadInfo call will fail and break the loop instead.
                return true;
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

        /// <summary>
        /// Localized "ETA {0}" segment; "--" while the speed is unknown, "0s" once nothing remains.
        /// </summary>
        private string BuildEtaSegment(ulong bytesDownloaded, ulong bytesTotal)
        {
            string value = "--";
            if (bytesTotal > 0 && bytesDownloaded >= bytesTotal)
            {
                value = "0s";
            }
            else if (bytesTotal > 0 && bytesPerSecond > 0f)
            {
                value = FormatDuration((bytesTotal - bytesDownloaded) / bytesPerSecond);
            }

            string template = localization.read(EtaTextKey);
            if (string.IsNullOrEmpty(template))
            {
                template = "ETA {0}";
            }

            return string.Format(template, value);
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return "--";
            }

            long totalSeconds = (long)Math.Ceiling(seconds);
            long hours = totalSeconds / 3600;
            long minutes = totalSeconds % 3600 / 60;
            long secs = totalSeconds % 60;

            if (hours > 0) return $"{hours}h {minutes:D2}m";
            if (minutes > 0) return $"{minutes}m {secs:D2}s";
            return $"{secs}s";
        }

        private void RenderDownloadLine(string downloadMessage, ulong bytesDownloaded, ulong bytesTotal)
        {
            char spinner = NextSpinnerChar();
            string line = BuildRenderLine(downloadMessage, bytesDownloaded, bytesTotal, spinner, includeEta: true);

            // A row that reaches the buffer width auto-wraps: the cursor ends up on the next row
            // (at column 0), every refresh then paints a new line and the console floods.
            // CJK/fullwidth characters count as two cells, so the width must be display-aware.
            int bufferWidth = TryGetBufferWidth();
            if (bufferWidth > 1)
            {
                int maxWidth = bufferWidth - 1;
                if (GetCellWidth(line) > maxWidth)
                {
                    line = BuildRenderLine(downloadMessage, bytesDownloaded, bytesTotal, spinner, includeEta: false);
                }

                line = ClampToCellWidth(line, maxWidth);
            }

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
            Console.Write(line);
        }

        private string BuildRenderLine(string downloadMessage, ulong bytesDownloaded, ulong bytesTotal, char spinner, bool includeEta)
        {
            StringBuilder line = new();
            line.Append(downloadMessage);
            line.Append(' ');
            line.Append(spinner);
            line.Append(' ');
            line.Append(DownloadProgressBar(bytesDownloaded, bytesTotal));
            line.Append(' ');
            line.Append(DownloadEstimate());

            if (includeEta)
            {
                line.Append(' ');
                line.Append(BuildEtaSegment(bytesDownloaded, bytesTotal));
            }

            line.Append("       "); // Speed and ETA change in size quite a bit, this is to prevent leftovers from the previous render
            return line.ToString();
        }

        private static int TryGetBufferWidth()
        {
            try
            {
                return Console.BufferWidth;
            }
            catch (Exception)
            {
                // Console output is redirected — the clamp is skipped and the line is written as-is.
                return -1;
            }
        }

        private static int GetCellWidth(string text)
        {
            int width = 0;
            foreach (char character in text)
            {
                width += IsWideChar(character) ? 2 : 1;
            }

            return width;
        }

        private static bool IsWideChar(char character)
        {
            return (character >= 0x1100 && character <= 0x115F)   // Hangul Jamo
                || (character >= 0x2E80 && character <= 0x303E)   // CJK radicals, Kangxi radicals, CJK symbols
                || (character >= 0x3041 && character <= 0x33FF)   // Hiragana through CJK compatibility
                || (character >= 0x3400 && character <= 0x4DBF)   // CJK extension A
                || (character >= 0x4E00 && character <= 0x9FFF)   // CJK unified ideographs
                || (character >= 0xA000 && character <= 0xA4CF)   // Yi syllables
                || (character >= 0xAC00 && character <= 0xD7A3)   // Hangul syllables
                || (character >= 0xF900 && character <= 0xFAFF)   // CJK compatibility ideographs
                || (character >= 0xFE30 && character <= 0xFE4F)   // CJK compatibility forms
                || (character >= 0xFF00 && character <= 0xFF60)   // fullwidth forms
                || (character >= 0xFFE0 && character <= 0xFFE6);  // fullwidth signs
        }

        /// <summary>
        /// Trims characters from the end until the display width fits, so a write can never
        /// reach the buffer's wrap boundary and break the in-place refresh.
        /// </summary>
        private static string ClampToCellWidth(string text, int maxWidth)
        {
            if (GetCellWidth(text) <= maxWidth)
            {
                return text;
            }

            StringBuilder clamped = new();
            int width = 0;
            foreach (char character in text)
            {
                int characterWidth = IsWideChar(character) ? 2 : 1;
                if (width + characterWidth > maxWidth)
                {
                    break;
                }

                clamped.Append(character);
                width += characterWidth;
            }

            return clamped.ToString();
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
