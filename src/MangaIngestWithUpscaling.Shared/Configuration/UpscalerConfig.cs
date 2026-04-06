namespace MangaIngestWithUpscaling.Shared.Configuration;

public enum GpuBackend
{
    Auto,
    CUDA,
    CUDA_12_8,
    ROCm,
    ROCm_GFX120X,
    XPU,
    CPU,
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
    ///     This is useful when using a manually managed Python environment that should not be recreated automatically.
    /// </summary>
    public bool ForceAcceptExistingEnvironment { get; set; } = false;

    public bool UseFp16 { get; set; } = true;
    public bool UseCPU { get; set; } = false;

    public string ModelsDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaIngestWithUpscaling",
            "Models"
        );

    public string PythonEnvironmentDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaIngestWithUpscaling",
            "Python-Env"
        );

    /// <summary>
    ///     Per-million-pixel inactivity timeout used to guard long-running upscaling operations.
    ///     The effective timeout is scaled by the largest image in the archive:
    ///     <c>effectiveTimeout = UpscaleTimeout × max(1, maxImagePixelCount / 1_000_000)</c>.
    ///     For example, with the default of 1 minute, an archive whose largest image is 2 MP
    ///     gets a 2-minute inactivity budget; an archive with only 0.5 MP images still gets
    ///     the full 1 minute.
    ///     The timeout fires when the upscaling process produces no output for the computed duration.
    /// </summary>
    public TimeSpan UpscaleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Maximum dimension (width or height) for images before upscaling.
    ///     Images larger than this will be resized to fit within this boundary while maintaining aspect ratio.
    ///     Set to null or 0 to disable this feature.
    /// </summary>
    public int? MaxDimensionBeforeUpscaling { get; set; } = null;

    /// <summary>
    ///     Optional image format conversion rules to apply during preprocessing.
    ///     Images matching the FromFormat will be converted to ToFormat before upscaling.
    ///     This only affects the temporary working copy, not the original files.
    ///     By default, PNG images are converted to JPG with quality 98 to ensure compatibility with the upscaler.
    /// </summary>
    public List<ImageFormatConversionRule> ImageFormatConversionRules { get; set; } =
    [
        new ImageFormatConversionRule
        {
            FromFormat = ".png",
            ToFormat = ".jpg",
            Quality = 98,
        },
        new ImageFormatConversionRule
        {
            FromFormat = ".avif",
            ToFormat = ".jpg",
            Quality = 98,
        },
    ];

    /// <summary>
    ///     When enabled, images that appear to have been cheaply upscaled (e.g. bicubic/bilinear) are
    ///     detected via a Laplacian-variance sharpness check and downscaled back toward their likely
    ///     native resolution before AI upscaling. This prevents double-upscaling artefacts and lets
    ///     the model see clean, high-contrast edges.
    /// </summary>
    public bool EnableSmartDownscale { get; set; } = false;

    /// <summary>
    ///     Sharpness threshold used by the smart downscale check. A standard deviation of the
    ///     Laplacian below this value is treated as evidence of a cheap upscale.
    ///     Lower values make detection stricter (fewer images downscaled);
    ///     higher values are more aggressive. Default is 15.0 – calibrate against your sources.
    /// </summary>
    public double SmartDownscaleThreshold { get; set; } = 15.0;

    /// <summary>
    ///     Scale factor applied when a cheap upscale is detected. 0.75 means the image is
    ///     reduced to 75 % of its current dimensions before being passed to the AI model.
    ///     Must be in the range (0, 1).
    /// </summary>
    public double SmartDownscaleFactor { get; set; } = 0.75;
}
