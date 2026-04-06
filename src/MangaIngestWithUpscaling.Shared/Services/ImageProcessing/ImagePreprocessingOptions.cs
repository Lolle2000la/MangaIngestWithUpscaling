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
}
