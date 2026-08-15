using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Drives a single long-running <c>worker.py</c> process (NDJSON over stdin/stdout) so that
/// PyTorch models and the GPU stay warm across consecutive upscale jobs instead of being
/// reinitialized per job.
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
    /// Gracefully shuts the worker process down and releases GPU resources.
    /// </summary>
    Task ShutdownWorkerAsync(CancellationToken cancellationToken);
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
}

public sealed record UpscaleJobFile(string Input, string Output, string Status);

public sealed record UpscaleJobResult(
    string Id,
    string Status,
    IReadOnlyList<UpscaleJobFile> Files,
    double ElapsedSeconds
);
