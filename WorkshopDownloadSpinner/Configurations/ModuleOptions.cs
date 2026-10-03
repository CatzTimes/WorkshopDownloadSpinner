namespace WorkshopDownloadSpinner.Configurations
{
    /// <summary>
    /// Tuning constants and localization keys of the download spinner.
    /// Consumed via "using static" so call sites keep their original names.
    /// </summary>
    public static class ModuleOptions
    {
        public const float DELAY_SECONDS = 1.5f;
        public const int PROGRESS_BAR_LENGTH = 16;
        public const float UPDATE_ESTIMATE_RATE_SECONDS = 2.5f;
        public const int RENDER_INTERVAL_MS = 100;
        public const int IDLE_POLL_INTERVAL_MS = 200;
        public const int STEAM_MEMORY_RELEASE_INTERVAL_MS = 1000;
        public const int SHUTDOWN_JOIN_TIMEOUT_MS = 1000;

        /// <summary>
        /// GetItemDownloadInfo can keep returning true with (0, 0) forever after an item finished
        /// (e.g. Steam re-flags it k_EItemStateNeedsUpdate without a download running), so a session
        /// that sees nothing but zeroes for this long is treated as finished regardless.
        /// </summary>
        public const int ALL_ZERO_STALL_TIMEOUT_MS = 10000;

        public const string DownloadingTextKey = "DownloadingText";
        public const string EtaTextKey = "EtaText";
    }
}
