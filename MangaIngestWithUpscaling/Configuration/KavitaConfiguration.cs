namespace MangaIngestWithUpscaling.Configuration;

public class KavitaConfiguration
{
    public const string Position = "Kavita";

    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiKey { get; set; }
}
