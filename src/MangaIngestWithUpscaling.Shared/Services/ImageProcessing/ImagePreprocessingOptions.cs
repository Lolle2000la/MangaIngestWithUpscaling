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
}
