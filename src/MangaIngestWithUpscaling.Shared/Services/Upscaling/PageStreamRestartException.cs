namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Signals that a page-streaming step must restart rather than fail terminally: the server uses it
/// when a finalize step hit a recoverable reason (the source archive changed, a spooled page is
/// missing, or the session was reset), and the worker uses it when the server rejected a page
/// non-terminally (for example the content or engine changed, or the request landed on a replica that
/// does not hold the chapter's spool). Restarting preserves the spool; reporting a failure deletes it.
/// </summary>
public sealed class PageStreamRestartException(string message, bool resetSpool = false)
    : Exception(message)
{
    /// <summary>
    /// True when the spool itself is unusable (for example the source archive changed) and should be
    /// discarded so the chapter restarts from scratch, rather than just re-fetching missing pages.
    /// </summary>
    public bool ResetSpool { get; } = resetSpool;
}
