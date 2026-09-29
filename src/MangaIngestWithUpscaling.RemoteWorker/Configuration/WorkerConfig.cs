namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public class WorkerConfig
{
    public static string SectionName => "WorkerConfig";

    public string ApiKey { get; set; } = null!;
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// Maximum number of attempts for a single upscaled-file upload before the task is reported as
    /// failed. Each retry resumes from the chunks the server already stored.
    /// </summary>
    public int UploadMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base delay before the first upload retry. Subsequent retries back off exponentially.
    /// </summary>
    public TimeSpan UploadRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lower bound for the per-attempt gRPC deadline, which is otherwise sized from the number of
    /// bytes still to send divided by <see cref="UploadMinThroughputBytesPerSecond"/>.
    /// </summary>
    public TimeSpan UploadTimeoutFloor { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Assumed worst-case upload throughput used to size the per-attempt gRPC deadline. The
    /// deadline scales with the remaining bytes so a legitimately slow transfer is not cut off at
    /// an arbitrary fixed time.
    /// </summary>
    public long UploadMinThroughputBytesPerSecond { get; set; } = 128 * 1024;
}
