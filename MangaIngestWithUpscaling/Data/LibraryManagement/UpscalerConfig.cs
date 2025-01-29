using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Data.LibraryManagement;

/// <summary>
/// Represents a preconfigured upscaler setting that can be associated with chapters.
/// </summary>
public class UpscalerConfig
{
    public int Id { get; set; }
    public required string Name { get; set; }             // An identifier for this specific config
    public UpscalerMethod UpscalerMethod { get; set; } = UpscalerMethod.MangaJaNai;   // e.g. "mangajanai"
    public required ScaleFactor ScalingFactor { get; set; }    // e.g. "1x", "2x"
    public required CompressionFormat CompressionFormat { get; set; } // e.g. "avid", "png", "webp"
    [Range(0, 100)]
    public required int Quality { get; set; }            // e.g. 80, 90
}

public enum UpscalerMethod
{
    MangaJaNai
}

/// <summary>
/// Possible upscaling factors for chapters.
/// </summary>
public enum ScaleFactor
{
    [Display(Name = "1x")]
    OneX,
    [Display(Name = "2x")]
    TwoX,
    [Display(Name = "3x")]
    ThreeX,
    [Display(Name = "4x")]
    FourX
}

/// <summary>
/// Possible compression formats when a chapter is upscaled.
/// </summary>
public enum CompressionFormat
{
    [Display(Name = "AVIF")]
    Avif,
    [Display(Name = "PNG")]
    Png,
    [Display(Name = "WebP")]
    Webp,
    [Display(Name = "JPEG")]
    Jpg
}