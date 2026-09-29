namespace MangaIngestWithUpscaling.Configuration;

public class UploadsConfig
{
    public const string Position = "Uploads";

    /// <summary>
    ///     How long partially uploaded chunks are kept before the cleanup pass removes them. Bounds
    ///     disk usage from uploads that are abandoned or cancelled without reporting a failure.
    ///     Defaults to 24 hours.
    /// </summary>
    public TimeSpan ChunkRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     How often the chunk cleanup pass runs. Defaults to one hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
