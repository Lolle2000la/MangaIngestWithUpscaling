namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Thrown when the server rejects a page non-terminally: the chapter must restart (for example the
/// content or engine changed, or the request landed on a replica that does not hold the chapter's
/// spool). The worker stops the chapter and lets the server requeue it rather than reporting a task
/// failure, which would delete the spool.
/// </summary>
public sealed class PageStreamRestartException(string message) : Exception(message);
