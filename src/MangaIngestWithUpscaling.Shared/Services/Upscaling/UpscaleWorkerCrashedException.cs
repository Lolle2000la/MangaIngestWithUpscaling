namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Thrown when the upscale worker process exits unexpectedly while a job is in flight. Unlike a
/// job-level error, a crash is recoverable: the caller should respawn the worker and resume the
/// chapter (the server keeps the already-spooled pages) rather than fail the task permanently.
/// </summary>
public sealed class UpscaleWorkerCrashedException(string message) : Exception(message);
