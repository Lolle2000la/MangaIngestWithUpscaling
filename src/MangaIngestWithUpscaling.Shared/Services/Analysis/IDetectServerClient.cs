using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// Drives a single long-running <c>detect_server.py</c> process so the page-break model stays
/// loaded across the pages of a chapter instead of being reloaded (and the interpreter restarted)
/// for every image, as the per-image CLI does.
/// </summary>
public interface IDetectServerClient
{
    /// <summary>
    /// Runs page-break detection for one image. A detection failure (unreadable image, inference
    /// error) is returned as a <see cref="SplitDetectionResult"/> with <c>Error</c> set, matching
    /// the CLI. Throws <see cref="DetectServerUnavailableException"/> when the server cannot be
    /// started or spoken to, so the caller can fall back to the CLI.
    /// </summary>
    Task<SplitDetectionResult> DetectAsync(string imagePath, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the server to return its cached VRAM to the driver while it stays warm, so a
    /// co-tenant upscaler can use the GPU. Returns <c>true</c> when acknowledged.
    /// </summary>
    Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken);

    /// <summary>Gracefully shuts the server down and releases GPU resources.</summary>
    Task ShutdownServerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the resident detection server is not usable (missing script/environment, spawn
/// failure, or a dead process). Callers fall back to the per-image CLI.
/// </summary>
public sealed class DetectServerUnavailableException : Exception
{
    public DetectServerUnavailableException(string message)
        : base(message) { }

    public DetectServerUnavailableException(string message, Exception inner)
        : base(message, inner) { }
}
