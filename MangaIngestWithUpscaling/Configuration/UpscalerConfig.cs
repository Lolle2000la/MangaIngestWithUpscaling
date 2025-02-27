
public record UpscalerConfig
{
    public const string Position = "Upscaler";

    public bool UseFp16 { get; set; } = true;
    public bool UseCPU { get; set; } = false;
    public int SelectedDeviceIndex = 0;

    public string ModelsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaIngestWithUpscaling",
        "Models"
        );

    public string PythonEnvironmentDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaIngestWithUpscaling",
        "Python-Env"
        );

    public TimeSpan UpscaleTimeout { get; set; } = TimeSpan.FromMinutes(1);
}