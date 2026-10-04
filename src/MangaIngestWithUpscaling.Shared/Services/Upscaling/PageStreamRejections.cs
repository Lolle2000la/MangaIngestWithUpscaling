namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// The single home for "how does a page-streaming rejection map to a <see cref="PageStreamDisposition"/>,
/// and how does a disposition map to the wire <c>terminal</c> flag". Keeping it here means the
/// server's response flag and the worker's failure classification cannot drift apart.
/// </summary>
public static class PageStreamRejections
{
    /// <summary>
    /// The wire contract: only a <see cref="PageStreamDisposition.Terminal"/> disposition sets
    /// <c>terminal = true</c>. A <see cref="PageStreamDisposition.Retry"/> is non-terminal, so the
    /// worker keeps the spool.
    /// </summary>
    public static bool ToWireTerminal(PageStreamDisposition disposition) =>
        disposition == PageStreamDisposition.Terminal;

    /// <summary>
    /// Maps a page-context resolution failure: a corrupt source archive or a task that is gone/terminal
    /// is deterministic; a momentarily unavailable source is a restart that preserves the spool.
    /// </summary>
    public static PageStreamDisposition ForResolution(bool taskTerminal, bool corrupt) =>
        corrupt || taskTerminal ? PageStreamDisposition.Terminal : PageStreamDisposition.Retry;
}
