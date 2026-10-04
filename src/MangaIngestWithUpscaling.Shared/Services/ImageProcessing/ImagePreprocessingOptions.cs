using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

/// <summary>
/// Options for preprocessing images before upscaling
/// </summary>
public class ImagePreprocessingOptions
{
    /// <summary>
    /// Maximum dimension (width or height) for resizing. Null means no resizing.
    /// </summary>
    public int? MaxDimension { get; set; }

    /// <summary>
    /// Image format conversion rules to apply
    /// </summary>
    public List<ImageFormatConversionRule> FormatConversionRules { get; set; } = [];

    /// <summary>
    /// When true, images that appear to have been cheaply upscaled are detected via a
    /// Laplacian-variance sharpness check and conditionally downscaled before AI upscaling.
    /// </summary>
    public bool EnableSmartDownscale { get; set; } = false;

    /// <summary>
    /// Laplacian standard-deviation threshold below which an image is considered cheaply upscaled.
    /// </summary>
    public double SmartDownscaleThreshold { get; set; } = 15.0;

    /// <summary>
    /// Scale factor applied when a cheap upscale is detected. Must be in (0, 1).
    /// </summary>
    public double SmartDownscaleFactor { get; set; } = 0.75;

    /// <summary>
    /// True when the configuration requests any per-image preprocessing (resize, format conversion
    /// or smart downscale).
    /// </summary>
    public static bool IsEnabled(UpscalerConfig config) => IsEnabled(FromConfig(config));

    /// <summary>
    /// True when these options request any per-image preprocessing (resize, format conversion or
    /// smart downscale).
    /// </summary>
    public static bool IsEnabled(ImagePreprocessingOptions options) =>
        options.MaxDimension is > 0
        || options.FormatConversionRules is { Count: > 0 }
        || options.EnableSmartDownscale;

    /// <summary>
    /// Builds the options the upscaler applies before invoking the worker. Shared by the whole-CBZ
    /// path and the page-streaming path so both preprocess identically.
    /// </summary>
    public static ImagePreprocessingOptions FromConfig(UpscalerConfig config) =>
        new()
        {
            MaxDimension = config.MaxDimensionBeforeUpscaling,
            FormatConversionRules = config.ImageFormatConversionRules ?? [],
            EnableSmartDownscale = config.EnableSmartDownscale,
            SmartDownscaleThreshold = config.SmartDownscaleThreshold,
            SmartDownscaleFactor = config.SmartDownscaleFactor,
        };
}
