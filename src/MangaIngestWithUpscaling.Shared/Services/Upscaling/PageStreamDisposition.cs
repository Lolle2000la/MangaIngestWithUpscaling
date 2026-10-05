namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// How a page-streaming rejection should be handled. Shared between the server (which writes the
/// wire <c>terminal</c> flag) and the worker (which classifies the failure), so both sides agree on
/// one meaning for "restart versus fail".
/// </summary>
public enum PageStreamDisposition
{
    /// <summary>
    /// Recoverable: keep the spool and let the server requeue the chapter, so the worker resumes at
    /// the first missing page instead of losing the already-upscaled pages.
    /// </summary>
    Retry,

    /// <summary>Deterministic: report the task failed, which clears the spool.</summary>
    Terminal,
}
