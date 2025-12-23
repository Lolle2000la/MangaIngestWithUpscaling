namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
/// Defines a rule for converting images from one format to another during preprocessing
/// </summary>
public record ImageFormatConversionRule
{
    /// <summary>
    /// The source image format to convert from (e.g., ".png", ".jpg")
    /// </summary>
    public string FromFormat { get; set; } = string.Empty;

    /// <summary>
    /// The target image format to convert to (e.g., ".png", ".jpg")
    /// </summary>
    public string ToFormat { get; set; } = string.Empty;

    /// <summary>
    /// Quality setting for lossy formats (1-100). Not used for lossless formats like PNG.
    /// </summary>
    public int? Quality { get; set; } = 95;
}
