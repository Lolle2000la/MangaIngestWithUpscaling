namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public class WorkerConfig
{
    public static string SectionName => "WorkerConfig";

    public string ApiKey { get; set; } = null!;
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// Hard cap on the number of attempts for a single upscaled-file upload. Each retry resumes
    /// from the chunks the server already stored, so retries are cheap.
    /// </summary>
    public int UploadMaxAttempts { get; set; } = 10;

    /// <summary>
    /// Base delay before the first upload retry. Subsequent retries back off exponentially.
    /// </summary>
    public TimeSpan UploadRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to keep retrying a failing upload before giving up and reporting the task failed.
    /// This bounds an outage rather than a fixed attempt count: a link that is down for longer than
    /// this still forces a re-download and re-upscale, so it defaults generously. The clock starts
    /// at the first failure, so a long but healthy initial attempt does not consume the budget; an
    /// attempt's deadline (<see cref="UploadTimeoutFloor"/> / throughput) is separate.
    /// </summary>
    public TimeSpan UploadRetryMaxElapsed { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Lower bound for the per-attempt gRPC deadline, which is otherwise sized from the full file
    /// size divided by <see cref="UploadMinThroughputBytesPerSecond"/>. The full size is used
    /// because the deadline also covers the server assembling the whole file.
    /// </summary>
    public TimeSpan UploadTimeoutFloor { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Assumed worst-case upload throughput used to size the per-attempt gRPC deadline. The
    /// deadline scales with the full file size (remaining transfer plus server-side assembly) so a
    /// legitimately slow transfer is not cut off at an arbitrary fixed time.
    /// </summary>
    public long UploadMinThroughputBytesPerSecond { get; set; } = 128 * 1024;
}
