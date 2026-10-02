using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopDownloadSpinner.Helpers
{
    public static class ConsoleHelper
    {
        public static bool DiscardConsoleInput
        {
            get;
            set
            {
                if (field == value) return;
                field = value;

                DiscardTokenSource.Cancel();
                DiscardTokenSource = new CancellationTokenSource();

                if (DiscardConsoleInput)
                {
                    Task.Run(() => DiscardConsoleInputs(DiscardTokenSource.Token));
                }
            }
        }

        private static CancellationTokenSource DiscardTokenSource { get; set; } = new();

        private static Task DiscardConsoleInputs(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested && Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
                {
                    // stdin is redirected or unavailable (service / docker / SSH without pty) — nothing to discard.
                    global::WorkshopDownloadSpinner.DiagnosticLog.Write("console input discard loop stopped (stdin unavailable)", exception);
                    return Task.CompletedTask;
                }

                // Poll gently; spinning here would pin a CPU core for the whole download.
                Thread.Sleep(50);
            }
            return Task.CompletedTask;
        }
    }
}
