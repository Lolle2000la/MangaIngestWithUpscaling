namespace MangaIngestWithUpscaling.Configuration;

public class KavitaConfiguration
{
    public const string Position = "Kavita";

    public bool Enabled { get; set; } = false;

    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiKey { get; set; }
}
