namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public class WorkerConfig
{
    public static string SectionName => "WorkerConfig";

    public string ApiKey { get; set; } = null!;
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// When enabled (the default), upscale tasks are processed as a page stream (fetch source
    /// pages, upload each upscaled page as it finishes) instead of transferring whole CBZ files.
    /// Requires a server that supports the page-streaming RPCs; when the server does not, the
    /// worker detects this at startup and falls back to the whole-CBZ path. Set to <c>false</c> to
    /// force whole-CBZ transfers.
    /// </summary>
    public bool UsePageStreaming { get; set; } = true;
}
