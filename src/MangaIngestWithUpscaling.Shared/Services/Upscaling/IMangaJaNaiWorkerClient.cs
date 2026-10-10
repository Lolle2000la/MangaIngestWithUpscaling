using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Drives upscale execution. The worker is an in-process ONNX runtime rather than a separate
/// process: a job and a chapter are the two shapes a caller can submit, and each keeps exactly one
/// model session cached while it runs. The session cache is deliberately bounded
/// (<see cref="OnnxSessionFactory.MaxCachedSessions"/>) and dropped after every chapter, so caching
/// is per-job rather than across jobs — holding a second model resident would double the device
/// memory budget for no gain, since consecutive chapters are the common case and reloading one
/// session is a couple of seconds.
/// </summary>
public interface IMangaJaNaiWorkerClient
{
    /// <summary>
    /// Submits an upscale job to the persistent worker and waits for its completion.
    /// </summary>
    /// <param name="request">The job to run.</param>
    /// <param name="progress">Optional sink for per-file progress events.</param>
    /// <param name="cancellationToken">Cancels the in-flight job. Cancellation is cooperative: the running tile finishes and no further tile is started.</param>
    /// <param name="timeout">Optional inactivity timeout; when exceeded the job is cancelled. The clock resets on progress, so a slow model is not mistaken for a stuck one.</param>
    Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    );

    /// <summary>
    /// Runs a chapter as a stream of pages: <paramref name="pages"/> is consumed and sent to the
    /// worker as it is produced, and <paramref name="onPageDone"/> is invoked as each page finishes
    /// so the caller can stream it back immediately. The call returns when the last page is done.
    /// </summary>
    /// <param name="request">Chapter output settings.</param>
    /// <param name="pages">The pages to process, in order.</param>
    /// <param name="progress">Optional sink for progress events.</param>
    /// <param name="onPageDone">Invoked for every finished page (upscaled or failed).</param>
    /// <param name="cancellationToken">Cancels the chapter. Cooperative as for <see cref="RunJobAsync"/>: an in-flight tile finishes, then no further tile is started.</param>
    /// <param name="timeout">Optional inactivity timeout; when exceeded the chapter is cancelled.</param>
    Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    );

    /// <summary>
    /// Releases the device memory the worker holds. With the session bounded to one model and
    /// dropped after every chapter there is no steady-state holding to free, so this is a no-op
    /// kept for callers that used to have to tear a worker process down before split detection.
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
