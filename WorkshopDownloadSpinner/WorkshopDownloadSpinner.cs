using SDG.Framework.Modules;
using SDG.Unturned;
using System;
using System.Reflection;
using global::WorkshopDownloadSpinner.Configurations;
using global::WorkshopDownloadSpinner.Diagnostics;
using global::WorkshopDownloadSpinner.Monitors;
using WorkshopDownloadSpinner.Services;

namespace WorkshopDownloadSpinner
{
    /// <summary>
    /// Harmony-free reimplementation of the original module.
    /// Instead of patching DedicatedUGC, a background watcher thread polls its currentDownload
    /// field via reflection and renders the console progress bar off the game's main thread.
    /// </summary>
    public class WorkshopDownloadSpinner : IModuleNexus
    {
        public static WorkshopDownloadSpinner Instance { get; private set; } = null!;
        private DownloadSpinnerService? SpinnerService { get; set; }

        public void initialize()
        {
            Instance = this;

            SDG.Framework.Modules.Module? module = ModuleHook.getModuleByName("WorkshopDownloadSpinner");
            string? moduleDirectory = module?.config.DirectoryPath;

            BeginDiagnostics(moduleDirectory, out Local localization);

            SpinnerService = CreateSpinnerService(localization);
            SpinnerService.StartWatching();

            DedicatedUGC.installed += OnWorkshopItemsInstalled;

            CommandWindow.Log($"Unturned Workshop Download Spinner {Assembly.GetExecutingAssembly().GetName().Version} by Gamingtoday093 & Catz has been Initialized");
        }

        public void shutdown()
        {
            DedicatedUGC.installed -= OnWorkshopItemsInstalled;

            if (SpinnerService != null)
            {
                SpinnerService.Shutdown();
                SpinnerService = null;
            }
        }

        private static void BeginDiagnostics(string? moduleDirectory, out Local localization)
        {
            localization = LoadLocalization(moduleDirectory);

            if (string.IsNullOrEmpty(moduleDirectory) || !DiagnosticLog.MarkerExists(moduleDirectory))
            {
                return;
            }

            if (DiagnosticLog.TryBegin(moduleDirectory))
            {
                CommandWindow.Log($"[WDSP] Diagnostic logging enabled: {DiagnosticLog.LogPath}");
                DiagnosticLog.Write($"initialize: assemblyVersion={Assembly.GetExecutingAssembly().GetName().Version}");
                DiagnosticLog.Write($"initialize: providerLanguage=\"{Provider.language}\", moduleDirectory=\"{moduleDirectory}\"");
                DiagnosticLog.Write($"initialize: downloadingText=\"{localization.read(ModuleOptions.DownloadingTextKey)}\", etaText=\"{localization.read(ModuleOptions.EtaTextKey)}\"");
            }
            else
            {
                // TryBegin already reported the file system error to the console.
                DiagnosticLog.Write("initialize: diagnostic log unavailable (marker found, log file creation failed)");
            }
        }

        private static DownloadSpinnerService CreateSpinnerService(Local localization)
        {
            DownloadSpinnerService service = new DownloadSpinnerService(localization);

            if (!service.CanWatchDedicatedUGC)
            {
                CommandWindow.LogWarning("WorkshopDownloadSpinner: DedicatedUGC.currentDownload field not found, progress bar disabled (Unturned internals changed?)");
                DiagnosticLog.Write("initialize: DedicatedUGC.currentDownload reflection field MISSING — watcher will never trigger");
            }
            else
            {
                DiagnosticLog.Write("initialize: DedicatedUGC.currentDownload reflection field resolved");
            }

            return service;
        }

        private static Local LoadLocalization(string? moduleDirectory)
        {
            if (string.IsNullOrEmpty(moduleDirectory))
            {
                DiagnosticLog.Write("initialize: module directory unknown, localization empty (progress texts fall back to hardcoded English)");
                return new Local();
            }

            // Reads {Provider.language}.dat (chosen by the -Lang= command line parameter) and
            // falls back to English.dat, exactly like the vanilla /modules command does.
            return Localization.tryRead(moduleDirectory, usePath: false);
        }

        private void OnWorkshopItemsInstalled()
        {
            // Broadcast once all workshop items are finished installing; the watcher winds down.
            DiagnosticLog.Write("event: DedicatedUGC.installed fired (all workshop items finished installing)");
            SpinnerService?.NotifyAllInstalled();
        }
    }
}
