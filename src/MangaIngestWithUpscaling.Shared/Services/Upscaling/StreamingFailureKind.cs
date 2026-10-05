namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>How a page-streaming failure should be handled.</summary>
public enum StreamingFailureKind
{
    /// <summary>A transport blip; requeue without reporting (the spool is preserved).</summary>
    Transient,

    /// <summary>A non-terminal rejection; restart the chapter without reporting.</summary>
    Restart,

    /// <summary>A deterministic failure; report it (which clears the spool).</summary>
    Permanent,
}
