namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Thrown when a page-streaming finalize step failed for a recoverable reason: the source archive
/// changed, a spooled page is missing, or the session was reset. The chapter should restart (the
/// worker re-manifests) rather than fail terminally, which would clear the spool and burn a retry.
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
