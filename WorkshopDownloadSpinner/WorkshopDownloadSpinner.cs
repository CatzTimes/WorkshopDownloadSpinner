using SDG.Framework.Modules;
using SDG.Unturned;
using System;
using System.Reflection;
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

            SpinnerService = CreateSpinnerService();
            SpinnerService.StartWatching();

            DedicatedUGC.installed += OnWorkshopItemsInstalled;

            CommandWindow.Log($"WorkshopDownloadSpinner {Assembly.GetExecutingAssembly().GetName().Version} by Gamingtoday093 has been Initialized");
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

        private static DownloadSpinnerService CreateSpinnerService()
        {
            Local localization = LoadLocalization();
            DownloadSpinnerService service = new DownloadSpinnerService(localization);

            if (!service.CanWatchDedicatedUGC)
            {
                CommandWindow.LogWarning("WorkshopDownloadSpinner: DedicatedUGC.currentDownload field not found, progress bar disabled (Unturned internals changed?)");
            }

            return service;
        }

        private static Local LoadLocalization()
        {
            SDG.Framework.Modules.Module? module = ModuleHook.getModuleByName("WorkshopDownloadSpinner");
            string? directoryPath = module?.config.DirectoryPath;
            if (string.IsNullOrEmpty(directoryPath))
            {
                return new Local();
            }

            // Reads {Provider.language}.dat (chosen by the -Lang= command line parameter) and
            // falls back to English.dat, exactly like the vanilla /modules command does.
            return Localization.tryRead(directoryPath, usePath: false);
        }

        private void OnWorkshopItemsInstalled()
        {
            // Broadcast once all workshop items are finished installing; the watcher winds down.
            SpinnerService?.NotifyAllInstalled();
        }
    }
}
