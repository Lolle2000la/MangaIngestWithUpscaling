using System.ComponentModel.DataAnnotations;

namespace MangaIngestWithUpscaling.Data.LibraryManagement
{
    /// <summary>
    /// Represents a preconfigured upscaler setting that can be associated with chapters.
    /// </summary>
    public class UpscalerConfig
    {
        public int Id { get; set; }
        public required string Name { get; set; }             // An identifier for this specific config
        public UpscalerMethod UpscalerMethod { get; set; } = UpscalerMethod.MangaJanai;   // e.g. "mangajanai"
        public required ScaleFactor ScalingFactor { get; set; }    // e.g. "1x", "2x"
        public required CompressionFormat CompressionFormat { get; set; } // e.g. "avid", "png", "webp"
        [Range(0, 100)]
        public required int Quality { get; set; }            // e.g. 80, 90
    }

    public enum UpscalerMethod
    {
        MangaJanai
    }

    /// <summary>
    /// Possible upscaling factors for chapters.
    /// </summary>
    public enum ScaleFactor
    {
        OneX,
        TwoX,
        ThreeX,
        FourX
    }

    /// <summary>
    /// Possible compression formats when a chapter is upscaled.
    /// </summary>
    public enum CompressionFormat
    {
        Avif,
        Png,
        Webp,
        Jpg
    }
}