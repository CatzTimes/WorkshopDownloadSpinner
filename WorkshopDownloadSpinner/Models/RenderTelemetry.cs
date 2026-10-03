namespace WorkshopDownloadSpinner.Models
{
    /// <summary>
    /// Render telemetry of the most recent progress-bar frame.
    /// Only ever touched by the watcher thread; read by the diagnostic milestone logging.
    /// </summary>
    public class RenderTelemetry
    {
        public int NaturalWidth;
        public int FinalWidth;
        public int BufferWidth = -1;
        public bool EtaDropped;
    }
}
