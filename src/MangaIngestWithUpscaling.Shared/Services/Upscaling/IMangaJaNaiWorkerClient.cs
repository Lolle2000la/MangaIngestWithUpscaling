using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Drives upscale execution. The worker is an in-process ONNX runtime rather than a separate
/// process: a job and a chapter are the two shapes a caller can submit, and each keeps exactly one
/// model session cached while it runs. The session cache is deliberately bounded
/// (<see cref="OnnxSessionFactory.MaxCachedSessions"/>) and released when the job or chapter ends,
/// so caching is per-unit-of-work rather than across units — holding a second model resident would
/// double the device memory budget for no gain, since consecutive chapters are the common case and
/// reloading one session is a couple of seconds.
/// <para>
/// <see cref="ReleaseGpuCacheAsync"/> is what performs that release for the streamed-chapter path.
/// The whole-CBZ path drops its session the same way, so both end up in the same state; only the
/// code that gets there differs.
/// </para>
/// </summary>
public interface IMangaJaNaiWorkerClient
{
    /// <summary>
    /// Submits an upscale job to the persistent worker and waits for its completion.
    /// </summary>
    /// <param name="request">The job to run.</param>
    /// <param name="progress">Optional sink for per-file progress events.</param>
    /// <param name="cancellationToken">Cancels the in-flight job. Cancellation is cooperative: the running tile finishes and no further tile is started.</param>
    /// <param name="timeout">Optional inactivity timeout; when exceeded the job is cancelled. The
    /// clock resets on the job's own progress — per page of a CBZ — rather than on whether a
    /// progress sink was passed, so a job submitted without one is not a hard wall-clock limit.</param>
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
    /// Releases the device memory the worker holds, and returns it to the driver. This is what the
    /// streamed-chapter path calls when a chapter ends; the whole-CBZ path drops its own session
    /// instead. Reloading a session afterwards costs a couple of seconds, so this is not something
    /// to call between pages — only between chapters.
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

/// <summary>
/// Outcome of a single page in an upscale job, as reported by <see cref="UpscaleJobFile.Status"/>.
/// <para>
/// The distinction is the contract between the producer that runs the engine and the streamed-chapter
/// consumer that decides what to upload: only <see cref="Success"/> means
/// <see cref="UpscaleJobFile.Output"/> holds upscaled pixels. Note that <see cref="Output"/> is set
/// even for a failure — the engine writes the fallback copy to the page's destination as it goes — so
/// the status, not the presence of a path, is what separates the two.
/// </para>
/// </summary>
public enum UpscaleJobStatus
{
    /// <summary>The page was decoded and upscaled; <see cref="UpscaleJobFile.Output"/> is the result.</summary>
    Success,

    /// <summary>The page could not be upscaled; the caller should pass its source bytes through.</summary>
    Error,
}

public sealed record UpscaleJobFile(string Input, string Output, UpscaleJobStatus Status);

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
