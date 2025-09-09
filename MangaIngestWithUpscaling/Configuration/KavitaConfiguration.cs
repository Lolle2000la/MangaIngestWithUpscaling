namespace MangaIngestWithUpscaling.Configuration;

public class KavitaConfiguration
{
    public const string Position = "Kavita";

    public bool Enabled { get; set; } = false;

    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiKey { get; set; }

    /// <summary>
    /// When true, attempts to update existing series metadata instead of just scanning folders.
    /// This preserves reading progress and other metadata when manga titles change.
    /// If false or if series update fails, falls back to folder scanning.
    /// </summary>
    public bool UseSeriesUpdate { get; set; } = true;
}
