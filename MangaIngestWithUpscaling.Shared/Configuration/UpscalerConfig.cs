namespace MangaIngestWithUpscaling.Shared.Configuration;

public enum GpuBackend
{
    Auto,
    CUDA,
    ROCm,
    CPU
}

public record UpscalerConfig
{
    public const string Position = "Upscaler";
    public int SelectedDeviceIndex = 0;

    /// <summary>
    ///     When enabled, the upscaler will only run on the remote worker. No local consumption will be attempted.
    ///     As a side effect, this will also disable automatic attempts to install necessary Python packages.
    /// </summary>
    public bool RemoteOnly { get; set; } = false;

    /// <summary>
    ///     Specifies which GPU backend to use for PyTorch. Auto will attempt to detect the best available option.
    /// </summary>
    public GpuBackend PreferredGpuBackend { get; set; } = GpuBackend.Auto;

    /// <summary>
    ///     When enabled, forces acceptance of existing Python environments without version or backend checks.
    ///     This is useful for Docker containers with pre-built environments that should not be recreated.
    /// </summary>
    public bool ForceAcceptExistingEnvironment { get; set; } = false;

    public bool UseFp16 { get; set; } = true;
    public bool UseCPU { get; set; } = false;

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