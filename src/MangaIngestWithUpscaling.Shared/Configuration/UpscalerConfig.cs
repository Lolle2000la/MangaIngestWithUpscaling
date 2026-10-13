using System.ComponentModel;
using System.Globalization;
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

    [Obsolete("Use WebGPU instead: this alias is remapped to it")]
    XPU = 103,
}

/// <summary>
/// The list of <see cref="ImageFormatConversionRule"/>s a <see cref="UpscalerConfig"/> carries, with
/// a type converter that maps the empty scalar the configuration binder emits for a JSON empty array
/// to an empty list.
/// <para>
/// The binder leaves a collection property untouched when its section has no children, so without
/// this <c>"ImageFormatConversionRules": []</c> would be indistinguishable from an absent key and
/// would keep the built-in defaults. An empty JSON array is surfaced as an empty string, which the
/// converter turns into an empty list, so "explicitly no rules" and "unset" stay distinct.
/// </para>
/// </summary>
[TypeConverter(typeof(ImageFormatConversionRuleListConverter))]
public class ImageFormatConversionRuleList : List<ImageFormatConversionRule> { }

/// <summary>
/// Converts the empty-string value the binder produces for an empty array into an empty
/// <see cref="ImageFormatConversionRuleList"/>. Only that scalar case reaches here: a non-empty array
/// has children and binds through the normal collection path. Any other scalar is rejected rather
/// than silently swallowed.
/// </summary>
public sealed class ImageFormatConversionRuleListConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value
    ) =>
        value is string { Length: 0 }
            ? new ImageFormatConversionRuleList()
            : base.ConvertFrom(context, culture, value);
}

public record UpscalerConfig
{
    public const string Position = "Upscaler";

    /// <summary>
    ///     Index of the device to use for upscaling, bound from
    ///     <c>Upscaler:SelectedDeviceIndex</c> (or <c>Ingest_Upscaler__SelectedDeviceIndex</c>).
    ///     <para>
    ///     The index is relative to the accelerators the runtime discovers for
    ///     <see cref="PreferredGpuBackend"/>: 0 is the CPU switch, 1 is the first accelerator,
    ///     2 the second, and so on.
    ///     </para>
    ///     Defaults to 1 (the first available accelerator).
    ///     Set to 0 to force CPU mode (equivalent to <see cref="UseCPU"/>); any value below 0
    ///     is treated as 0.
    /// </summary>
    public int SelectedDeviceIndex { get; set; } = 1;

    /// <summary>
    ///     When enabled, the upscaler will only run on the remote worker. No local consumption will be attempted.
    ///     As a side effect, this will also disable automatic attempts to download models locally.
    /// </summary>
    public bool RemoteOnly { get; set; } = false;

    /// <summary>
    ///     Specifies which GPU backend / execution provider to use for ONNX Runtime. Auto will attempt to detect the best available option.
    /// </summary>
    public GpuBackend PreferredGpuBackend { get; set; } = GpuBackend.Auto;

    /// <summary>
    ///     When true, uses 16-bit floating point (FP16) half-precision models.
    ///     When false, uses 32-bit floating point (FP32) single-precision models.
    ///     When null (default), automatically detects whether FP16 inference is supported
    ///     by the active hardware/GPU backend.
    /// </summary>
    public bool? UseFp16 { get; set; } = null;

    /// <summary>
    ///     Gets the effective FP16 setting, auto-detecting hardware support if <see cref="UseFp16"/> is null.
    ///     <para>
    ///     Use <see cref="ResolveUseFp16"/> where the resolved execution provider is at hand: the
    ///     probe below then answers for the provider that will actually run, instead of for whichever
    ///     one the preference happens to name.
    ///     </para>
    /// </summary>
    public bool ResolvedUseFp16 => ResolveUseFp16(PreferredGpuBackend);

    /// <summary>
    ///     <see cref="ResolvedUseFp16"/> for an execution provider that has already been resolved.
    /// </summary>
    /// <param name="resolvedBackend">
    ///     The provider in use, e.g. from <c>IOnnxSessionFactory.GetEffectiveBackend()</c>. Pass it
    ///     unresolved (<see cref="GpuBackend.Auto"/>) only when it has not been probed yet.
    /// </param>
    public bool ResolveUseFp16(GpuBackend resolvedBackend) =>
        UseFp16
        ?? Fp16CapabilityDetector.IsFp16Supported(
            resolvedBackend,
            Math.Max(0, SelectedDeviceIndex - 1),
            UseCPU || SelectedDeviceIndex <= 0
        );

    public bool UseCPU { get; set; } = false;

    /// <summary>
    ///     Tile size for ONNX upscaling in pixels.
    ///     0 = Auto-estimate from the model's activation footprint and available VRAM (default).
    ///     &gt; 0 = Explicit maximum tile size (e.g. 512, 768, 1024).
    ///     Any negative value = force single pass (no tiling); -1 is the conventional one.
    ///     The automatic value is derived, not clamped: see docs/UPSCALING_MEMORY_MODEL.md.
    /// </summary>
    public int TileSize { get; set; } = 0;

    /// <summary>
    ///     Memory budget for automatic tile size estimation, as a byte count or a size string
    ///     (<c>1GiB</c>, <c>12 GiB</c>, <c>12288MiB</c>). Unset or empty means 0
    ///     (<see cref="ResolvedMemoryBudgetBytes"/>), which tells tile sizing to detect the budget
    ///     from the device instead. A configured value that cannot be parsed is rejected at startup
    ///     by <see cref="UpscalerConfigValidator"/>, so it never reaches
    ///     <see cref="ResolvedMemoryBudgetBytes"/>.
    /// </summary>
    public string? MemoryBudgetBytes { get; set; }

    /// <summary>
    ///     Gets the parsed memory budget. 0 when nothing usable is configured, which is the signal
    ///     for tile sizing to detect the budget from the device instead.
    /// </summary>
    public long ResolvedMemoryBudgetBytes =>
        ByteSize.TryParse(MemoryBudgetBytes, out ByteSize parsed) ? parsed.Bytes : 0;

    /// <summary>
    ///     Optional fraction of free GPU VRAM to utilize for automatic tile budgeting.
    ///     Unset (or exactly 0.0) means automatic adaptive budgeting: ~97% when headless/NAS, ~95% with
    ///     a 512MB-1GB safety margin on desktop. A positive value must be at least 0.1 — the budget it
    ///     computes is clamped to that floor, so anything below it would apply a different fraction
    ///     than the one configured and is rejected at startup instead. Set to 1.0 on dedicated
    ///     machines (like a NAS) to utilize 100% of free VRAM.
    /// </summary>
    public double? VramUtilizationFraction { get; set; } = null;

    /// <summary>
    ///     Forces the one-off per-device memory benchmark to run again on the next upscale, replacing
    ///     the stored calibration for this device. The benchmark itself is automatic: it runs the
    ///     first time a device is seen and is cached per device in <c>device-memory-profile.json</c>
    ///     next to the models. Only set this to re-measure, e.g. after a driver or model-set change.
    ///     <para>
    ///     The calibration is honoured once, not continuously: configuration is read-only and never
    ///     written back, so the benchmark runs on the next upscale and then stays out of the way for
    ///     the rest of the process — the flag does not need clearing, and clearing it while the app
    ///     runs does not stop that one re-measurement.
    ///     </para>
    /// </summary>
    public bool RecalibrateDeviceMemory { get; set; } = false;

    /// <summary>
    ///     Optional safety margin subtracted from free VRAM when estimating tile budget, as a byte
    ///     count or a size string (<c>512MiB</c>, <c>1.5 GiB</c>). Unset means the margin is chosen
    ///     automatically (256 MiB headless, 1.5-3.0 GiB with a desktop attached).
    /// </summary>
    public string? VramSafetyMarginBytes { get; set; }

    public long? ResolvedVramSafetyMarginBytes =>
        ByteSize.TryParse(VramSafetyMarginBytes, out ByteSize parsed) ? parsed.Bytes : null;

    /// <summary>
    ///     VRAM usage below which the GPU is treated as quasi-exclusive to the upscaler, as a byte
    ///     count or a size string (<c>700MiB</c>, <c>1 GiB</c>). Unset means 350 MiB (driver plus a
    ///     display server or a couple of light helpers).
    ///     <para>
    ///     At or below it the budget keeps only a minimal reserve (256 MiB); above it the budget keeps
    ///     a desktop-sized safety margin (15% of total VRAM, clamped to 1.5-3.0 GiB), because drivers
    ///     start evicting into GTT near 85-90% utilisation. Raise this when another process reliably
    ///     shares the card but stays bounded — e.g. a video transcoder holding 700 MiB: at
    ///     <c>1GiB</c> the upscaler budgets the card as if exclusive again.
    ///     </para>
    /// </summary>
    public string? VramExclusiveThresholdBytes { get; set; }

    public long? ResolvedVramExclusiveThresholdBytes =>
        ByteSize.TryParse(VramExclusiveThresholdBytes, out ByteSize parsed) ? parsed.Bytes : null;

    public string ModelsDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaIngestWithUpscaling",
            "Models"
        );

    /// <summary>
    /// The models directory as an absolute path. A relative configured value is resolved against the process CWD.
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

    /// <summary>
    ///     Order in which model architectures are preferred when several are available for the same
    ///     scale and model version, most preferred first, as a comma-separated list of architecture
    ///     names: <c>FDAT_XL,FDAT_M,ESRGAN</c>.
    ///     <para>
    ///     Recognised names are <c>ESRGAN</c>, <c>SPAN</c>, <c>FDAT_M</c>, <c>FDAT_XL</c>,
    ///     <c>DAT2</c> and <c>HAT_L</c> (case-insensitive; <c>FDAT_M</c>/<c>FDATM</c> and
    ///     <c>FDAT_XL</c>/<c>FDATXL</c> are equivalent). An unrecognised name fails startup.
    ///     Architectures the value does not name keep their built-in relative order after the listed
    ///     ones, so a partial list re-ranks without dropping models.
    ///     </para>
    ///     <para>
    ///     The default (<c>FDAT_M,ESRGAN,SPAN,FDAT_XL,DAT2,HAT_L</c>) puts the fast, GPU-native
    ///     architectures ahead of the transformers, which are considerably slower. Promote
    ///     <c>FDAT_XL</c> — the most useful candidate to promote — when its quality is worth the time
    ///     on this device.
    ///     </para>
    ///     <para>
    ///     The model <em>version</em> order (V3detail, V3denoise, V2standard, V1) is not configurable
    ///     and stays the outer one, so a promoted architecture is preferred within every version.
    ///     </para>
    /// </summary>
    public string? PreferredModelArchitectures { get; set; }

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
    public string? MaxSpoolBytesPerTask { get; set; } = "8 GiB";

    public long ResolvedMaxSpoolBytesPerTask =>
        ByteSize.TryParse(MaxSpoolBytesPerTask, out ByteSize parsed)
            ? parsed.Bytes
            : 8L * 1024 * 1024 * 1024;

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
    ///     The built-in image format conversion rules, used when
    ///     <see cref="ImageFormatConversionRules"/> is unset: PNG and AVIF images are converted to
    ///     JPG with quality 98 to ensure compatibility with the upscaler.
    /// </summary>
    public static IReadOnlyList<ImageFormatConversionRule> DefaultImageFormatConversionRules { get; } =
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
    ///     Optional image format conversion rules to apply during preprocessing.
    ///     Images matching the FromFormat will be converted to ToFormat before upscaling.
    ///     This only affects the temporary working copy, not the original files.
    ///     <para>
    ///     Unset (<see langword="null"/>) means the built-in
    ///     <see cref="DefaultImageFormatConversionRules"/>; an explicit empty list disables conversion.
    ///     The default is <see langword="null"/> rather than the built-in list because the
    ///     configuration binder appends to a non-null collection, which would make
    ///     <c>"ImageFormatConversionRules": []</c> silently keep the built-in rules. Read
    ///     <see cref="EffectiveImageFormatConversionRules"/> instead of this property.
    ///     </para>
    /// </summary>
    public ImageFormatConversionRuleList? ImageFormatConversionRules { get; set; }

    /// <summary>
    ///     <see cref="ImageFormatConversionRules"/> with the built-in defaults substituted when it is
    ///     unset. An explicit empty list stays empty.
    /// </summary>
    public IReadOnlyList<ImageFormatConversionRule> EffectiveImageFormatConversionRules =>
        ImageFormatConversionRules ?? DefaultImageFormatConversionRules;

    /// <summary>
    ///     When enabled, images that appear to have been cheaply upscaled (e.g. bicubic/bilinear) are
    ///     detected via a Laplacian-variance sharpness check and downscaled back toward their likely
    ///     native resolution before AI upscaling. This prevents double-upscaling artefacts and lets
    ///     the model see clean, high-contrast edges.
    ///     Default: true.
    /// </summary>
    public bool EnableSmartDownscale { get; set; } = true;

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
