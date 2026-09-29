namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public class WorkerConfig
{
    public static string SectionName => "WorkerConfig";

    public string ApiKey { get; set; } = null!;
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// When enabled, upscale tasks are processed as a page stream (fetch source pages, upload each
    /// upscaled page as it finishes) instead of transferring whole CBZ files. Requires a server
    /// that supports the page-streaming RPCs; when the server does not, the worker falls back to
    /// the whole-CBZ path.
    /// </summary>
    public bool UsePageStreaming { get; set; } = false;
}
