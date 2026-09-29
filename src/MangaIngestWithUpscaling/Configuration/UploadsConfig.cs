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

    /// <summary>
    ///     Upper bound on the chunk number or declared total chunk count a single task's upload may
    ///     use. Because the gRPC request-body size cap is lifted for uploads, this is what keeps a
    ///     misbehaving (but authenticated) worker from filling the disk with chunks that persist
    ///     until <see cref="ChunkRetention"/>. Defaults to 16384, i.e. about 16 GiB at the worker's
    ///     1 MiB chunk size.
    /// </summary>
    public int MaxTotalChunks { get; set; } = 16384;

    /// <summary>
    ///     Upper bound on the payload of a single uploaded chunk. Defaults to 16 MiB, well above the
    ///     worker's 1 MiB chunk so other clients have headroom.
    /// </summary>
    public int MaxChunkBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    ///     Upper bound on the total bytes of chunks stored for a single task. This is the effective
    ///     per-task disk bound, independent of the per-chunk limit. Defaults to 16 GiB, matching
    ///     <see cref="MaxTotalChunks"/> at the worker's 1 MiB chunk size.
    /// </summary>
    public long MaxTaskBytes { get; set; } = 16L * 1024 * 1024 * 1024;
}
