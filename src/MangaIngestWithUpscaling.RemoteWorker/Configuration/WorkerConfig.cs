namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public class WorkerConfig
{
    public static string SectionName => "WorkerConfig";

    public string ApiKey { get; set; } = null!;
    public string ApiUrl { get; set; } = null!;
}
