using MangaIngestWithUpscaling.Shared.Services.GPU;

namespace MangaIngestWithUpscaling.Shared.Configuration;

public enum GpuBackend
{
    Auto,
    WebGPU,
    CUDA,
    DirectML,
    OpenVINO,
    MIGraphX,
    CPU,

    // Legacy PyTorch aliases retained for backwards compatibility with existing configuration files
    [Obsolete("Use WebGPU or Auto instead")]
    ROCm = 100,

    [Obsolete("Use WebGPU or Auto instead")]
    ROCm_GFX120X = 101,

    [Obsolete("Use CUDA or Auto instead")]
    CUDA_12_8 = 102,

    [Obsolete("Use WebGPU or OpenVINO instead")]
    XPU = 103,
}

public record UpscalerConfig
{
    public const string Position = "Upscaler";

    /// <summary>
    ///     Index of the device to use for upscaling.
    ///     The device list is ordered with CPU at index 0, followed by accelerators (CUDA, ROCm, XPU, MPS).
    ///     Defaults to 1 (the first available GPU/accelerator).
    ///     Set to 0 to force CPU mode (equivalent to <see cref="UseCPU"/>).
    /// </summary>
    public int SelectedDeviceIndex = 1;

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

    /// <summary>
    ///     When true, uses 16-bit floating point (FP16) half-precision models.
    ///     When false, uses 32-bit floating point (FP32) single-precision models.
    ///     When null (default), automatically detects whether FP16 inference is supported
    ///     by the active hardware/GPU backend.
    /// </summary>
    public bool? UseFp16 { get; set; } = null;

    /// <summary>
    ///     Gets the effective FP16 setting, auto-detecting hardware support if <see cref="UseFp16"/> is null.
    /// </summary>
    public bool ResolvedUseFp16 =>
        UseFp16
        ?? Fp16CapabilityDetector.IsFp16Supported(
            Math.Max(0, SelectedDeviceIndex - 1),
            PreferredGpuBackend,
            UseCPU || SelectedDeviceIndex <= 0
        );

    public bool UseCPU { get; set; } = false;

    /// <summary>
    ///     Tile size for ONNX upscaling in pixels.
    ///     0 = Auto-estimate based on model architecture, scaling factor, and available VRAM (default).
    ///     &gt; 0 = Explicit maximum tile size (e.g. 512, 768, 1024).
    ///     -1 = Force single pass (no tiling).
    /// </summary>
    public int TileSize { get; set; } = 0;

    /// <summary>
    ///     Memory budget in bytes for automatic tile size estimation.
    ///     0 = Auto-detect from GPU / system VRAM (default).
    /// </summary>
    public long MemoryBudgetBytes { get; set; } = 0;

    /// <summary>
    ///     Optional fraction of free GPU VRAM to utilize for automatic tile budgeting (0.0 to 1.0).
    ///     Defaults to null (automatic adaptive budgeting: ~97% when headless/NAS, ~95% with 512MB-1GB safety margin on desktop).
    ///     Set to 1.0 on dedicated machines (like a NAS) to utilize 100% of free VRAM.
    /// </summary>
    public double? VramUtilizationFraction { get; set; } = null;

    /// <summary>
    ///     Optional safety margin in bytes subtracted from free VRAM when estimating tile budget.
    ///     Defaults to null (automatically chosen: 256 MB in headless environments, 512 MB-1024 MB when desktop is active).
    /// </summary>
    public long? VramSafetyMarginBytes { get; set; } = null;

    public string ModelsDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaIngestWithUpscaling",
            "Models"
        );

    /// <summary>
    /// The models directory as an absolute path. A relative configured value is resolved against the
    /// process CWD so the engine identity and the spawned Python worker agree on one directory.
    /// </summary>
    public string ResolvedModelsDirectory
    {
        get
        {
            try
            {
                return Path.GetFullPath(ModelsDirectory);
            }
            catch (Exception)
            {
                return ModelsDirectory;
            }
        }
    }

    public string PythonEnvironmentDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaIngestWithUpscaling",
            "Python-Env"
        );

    /// <summary>
    /// Dedicated, app-owned directory the page-streaming spool is written to. The app creates its own
    /// <c>{process id}-{guid}</c> roots under it and sweeps only those, so do not point it at a
    /// directory that holds unrelated data. Defaults to a subdirectory of the system temp directory.
    /// Set this to a path on a real disk when <c>/tmp</c> is a RAM-backed tmpfs, so spooling large
    /// chapters cannot exhaust memory.
    /// </summary>
    public string? SpoolDirectory { get; set; }

    /// <summary>
    /// Upper bound on the bytes spooled for one streamed task, so a single chapter cannot fill the
    /// disk. Defaults to 8 GiB. There is no process-wide cap; see
    /// <c>docs/PAGE_STREAMING_KNOWN_LIMITATIONS.md</c>.
    /// </summary>
    public long MaxSpoolBytesPerTask { get; set; } = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a worker waits for a page manifest. The manifest normally returns immediately, but
    /// when a chapter is already fully spooled the server assembles the CBZ before answering, which
    /// can exceed a short deadline for a large chapter on slow storage — the worker would then
    /// re-stream (and eventually fail) a chapter that was already complete.
    /// </summary>
    public TimeSpan ManifestTimeout { get; set; } = TimeSpan.FromMinutes(10);

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
    ///     When enabled and output format is AVIF, encodes images with 10-bit depth
    ///     directly from the model's high-precision float output, eliminating 8-bit quantization
    ///     banding and reducing ringing artifacts on fine lines and smooth gradients.
    ///     Defaults to false (standard 8-bit AVIF).
    /// </summary>
    public bool Enable10BitAvif { get; set; } = false;

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

    /// <summary>
    ///     How long the persistent upscale worker process may sit idle (no in-flight or queued
    ///     job) before it is shut down to release GPU/VRAM resources. The process is respawned
    ///     lazily on the next job, so this only affects idle resource usage.
    /// </summary>
    public TimeSpan WorkerIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Maximum time a single request to the resident detection server may run before the request
    ///     is cancelled and the server process killed; the caller then falls back to the per-image
    ///     CLI, so a wedged detector cannot hang a task forever. Zero disables the guard.
    /// </summary>
    public TimeSpan DetectServerRequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Maximum number of jobs the persistent upscale worker may have in flight plus queued
    ///     at once. Upscaling is processed sequentially, so this is normally 1.
    /// </summary>
    public int WorkerQueueCapacity { get; set; } = 1;

    /// <summary>
    ///     When enabled, the worker preloads all chain models before accepting its first job
    ///     (worker.py --warmup). Disabled by default: models are loaded lazily on first use and
    ///     cached in the engine, so consecutive jobs stay warm without paying the cold-start cost
    ///     of loading every model up front.
    /// </summary>
    public bool WorkerWarmup { get; set; } = false;

    /// <summary>
    ///     When enabled, every line the worker writes to stderr is mirrored to the host's stderr
    ///     regardless of the configured log level. Defaults to on in Development and off elsewhere;
    ///     either can be overridden explicitly via configuration.
    /// </summary>
    public bool WorkerLogToStderr { get; set; } =
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    ///     How long the persistent upscale worker must sit idle (no in-flight job) before it
    ///     returns its cached allocator blocks (VRAM) to the driver, so co-tenant GPU processes
    ///     (e.g. split detection) can run while the worker stays warm. Zero disables the release.
    ///     The worker can also be asked to release immediately via
    ///     <see cref="IMangaJaNaiWorkerClient.ReleaseGpuCacheAsync"/>.
    /// </summary>
    public TimeSpan WorkerIdleCacheReleaseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     When true, split detection shuts the persistent upscaling worker down entirely before
    ///     running, guaranteeing maximum free VRAM. Intended for very VRAM-limited GPUs where even
    ///     an idle worker (model weights + CUDA context) would starve detection. When false (the
    ///     default), detection only asks the worker to release its cached VRAM and both processes
    ///     coexist.
    /// </summary>
    public bool ShutdownWorkerBeforeSplitDetection { get; set; } = false;
}
