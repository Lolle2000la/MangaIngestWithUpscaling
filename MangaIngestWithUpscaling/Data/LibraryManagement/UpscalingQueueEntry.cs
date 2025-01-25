namespace MangaIngestWithUpscaling.Data.LibraryManagement
{
    /// <summary>
    /// Represents an entry in the upscaling queue, referencing a chapter and
    /// an upscaler configuration, along with the time it was queued.
    /// </summary>
    public class UpscalingQueueEntry
    {
        public int Id { get; set; }
        public int ChapterId { get; set; }
        public Chapter Chapter { get; set; }

        public int UpscalerConfigId { get; set; }
        public UpscalerConfig UpscalerConfig { get; set; }

        public DateTime QueuedAt { get; set; }
    }
}