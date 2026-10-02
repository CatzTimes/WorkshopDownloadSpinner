using System;
using System.IO;
using System.Text;

namespace WorkshopDownloadSpinner.Services
{
    /// <summary>
    /// Wraps the game's Console.Out writer so that log lines always start on their own row:
    /// while a progress-bar row is on screen (left open for in-place refresh), any log line
    /// first terminates that row with a newline. Without this, log lines written by the
    /// game's console thread are appended behind the progress bar.
    /// The renderer coordinates through <see cref="Sync"/> and <see cref="ProgressRowActive"/>.
    /// </summary>
    internal sealed class ConsoleOutProxy : TextWriter
    {
        private readonly TextWriter inner;

        public override Encoding Encoding => inner.Encoding;

        public static object Sync { get; } = new object();

        /// <summary>Guarded by <see cref="Sync"/>. True while an unfinished progress row is on screen.</summary>
        public static bool ProgressRowActive { get; set; }

        public ConsoleOutProxy(TextWriter inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public static void Install()
        {
            try
            {
                if (Console.Out is ConsoleOutProxy)
                {
                    return;
                }

                Console.SetOut(new ConsoleOutProxy(Console.Out));
                DiagnosticLog.Write("console proxy installed: log lines now start on their own row");
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("installing the console proxy failed", exception);
            }
        }

        /// <summary>Ends the progress-bar row with a newline if one is on screen.</summary>
        public static void EndProgressRow()
        {
            lock (Sync)
            {
                EndProgressRowUnsafe();
            }
        }

        private static void EndProgressRowUnsafe()
        {
            if (!ProgressRowActive)
            {
                return;
            }

            ProgressRowActive = false;
            try
            {
                // Routes back through WriteLine() (reentrant on this thread) for one bare newline.
                Console.WriteLine();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("ending the progress row threw", exception);
            }
        }

        public override void Write(char value)
        {
            lock (Sync)
            {
                inner.Write(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }

            lock (Sync)
            {
                inner.Write(value);
            }
        }

        public override void WriteLine()
        {
            lock (Sync)
            {
                inner.WriteLine();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (Sync)
            {
                EndProgressRowUnsafe();
                inner.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (Sync)
            {
                inner.Flush();
            }
        }
    }
}
