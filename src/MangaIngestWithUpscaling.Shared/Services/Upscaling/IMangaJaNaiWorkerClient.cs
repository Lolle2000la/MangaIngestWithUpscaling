using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Drives upscale execution so that models and GPU sessions stay cached across
/// consecutive upscale jobs instead of being reinitialized per job.
/// </summary>
public interface IMangaJaNaiWorkerClient
{
    /// <summary>
    /// Submits an upscale job to the persistent worker and waits for its completion.
    /// </summary>
    /// <param name="request">The job to run.</param>
    /// <param name="progress">Optional sink for per-file progress events.</param>
    /// <param name="cancellationToken">Cancels the in-flight job (sends a <c>cancel</c> request).</param>
    /// <param name="timeout">Optional inactivity timeout; when exceeded the job is cancelled and the worker restarted.</param>
    Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    );

    /// <summary>
    /// Runs a chapter as a stream of pages: <paramref name="pages"/> is consumed and sent to the
    /// worker as it is produced, and <paramref name="onPageDone"/> is invoked (from the worker's
    /// stdout reader thread) as each page finishes so the caller can stream it back immediately.
    /// The call returns when the worker emits its final <c>done</c> event.
    /// </summary>
    /// <param name="request">Chapter output settings.</param>
    /// <param name="pages">The pages to process, in order.</param>
    /// <param name="progress">Optional sink for progress events.</param>
    /// <param name="onPageDone">Invoked for every finished page (upscaled or failed).</param>
    /// <param name="cancellationToken">Cancels the chapter (sends a <c>cancel</c> request).</param>
    /// <param name="timeout">Optional inactivity timeout; when exceeded the chapter is cancelled and the worker restarted.</param>
    Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    );

    /// <summary>
    /// Gracefully shuts the worker process down and releases GPU resources.
    /// </summary>
    Task ShutdownWorkerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Shuts the worker process down, optionally tearing it down even while a job is in flight. A
    /// forced shutdown is used to free VRAM before split detection on VRAM-limited setups; the
    /// in-flight task is requeued. Defaults to the graceful path for clients that do not distinguish.
    /// </summary>
    Task ShutdownWorkerAsync(bool force, CancellationToken cancellationToken) =>
        ShutdownWorkerAsync(cancellationToken);

    /// <summary>
    /// Asks the running worker to return its cached allocator blocks (VRAM) to the driver so
    /// co-tenant GPU processes can run, while keeping the worker warm. Returns <c>true</c> when
    /// the worker acknowledged the release, <c>false</c> when no worker is running, a job is in
    /// flight, or the request failed/timed out.
    /// </summary>
    Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Describes a single upscale job in terms of the worker's "simple form" protocol.
/// </summary>
public sealed record UpscaleJobRequest
{
    public required string Id { get; init; }
    public required string InputPath { get; init; }
    public required string OutputFolder { get; init; }
    public string OutputFilename { get; init; } = "%filename%";
    public required CompressionFormat Format { get; init; }
    public required ScaleFactor Scale { get; init; }
    public bool Overwrite { get; init; } = true;

    /// <summary>Lossy compression quality; null uses the worker's workflow default.</summary>
    public int? Quality { get; init; }
}

public sealed record UpscaleJobFile(string Input, string Output, string Status);

/// <summary>Output settings for a streamed chapter. Pages arrive later via the page stream.</summary>
public sealed record ChapterJobRequest
{
    public required string Id { get; init; }
    public required string OutputFolder { get; init; }
    public required CompressionFormat Format { get; init; }
    public required ScaleFactor Scale { get; init; }
    public int TotalPages { get; init; }

    /// <summary>Lossy compression quality; null uses the worker's workflow default.</summary>
    public int? Quality { get; init; }
}

/// <summary>One page of a streamed chapter.</summary>
public sealed record ChapterPage(int Index, string Name, string Path);

public sealed record UpscaleJobResult(
    string Id,
    string Status,
    IReadOnlyList<UpscaleJobFile> Files,
    double ElapsedSeconds
);
