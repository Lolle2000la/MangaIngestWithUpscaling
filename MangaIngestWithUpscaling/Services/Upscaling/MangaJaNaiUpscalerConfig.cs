using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Configuration settings for the Upscale Manga workflow.
/// This directly maps to the JSON structure of the workflow configuration used by MangaJaNaiConvertGui.
/// 
/// The explanation on the JSON structure can be found in the MangaJaNaiConvertGui project. Explanations of the properties are lifted from there.
/// </summary>
public class MangaJaNaiUpscalerConfig
{
    /// <summary>
    /// Choose if you want to upscale a single file or a whole folder 
    /// - 0 - file
    /// - 1 - folder
    /// </summary>
    public int? SelectedTabIndex { get; set; }
    /// <summary>
    /// absolute file path. Used when **SelectedTabIndex** = 0
    /// </summary>
    public string? InputFilePath { get; set; }
    /// <summary>
    /// absolute folder path. Used when **SelectedTabIndex** = 1
    /// </summary>
    public string? InputFolderPath { get; set; }
    /// <summary>
    /// Name of generated filenames. Keep `%filename%` to leave the same name
    /// </summary>
    public string? OutputFilename { get; set; }
    /// <summary>
    /// absolute folder path.
    /// </summary>
    public string? OutputFolderPath { get; set; }
    public bool? OverwriteExistingFiles { get; set; }
    /// <summary>
    /// true/false - needs to be true for upscale to work
    /// </summary>
    public bool? UpscaleImages { get; set; }
    /// <summary>
    /// true/false. Only one should be true. Selects output filetype.
    /// </summary>
    public bool? WebpSelected { get; set; }
    /// <summary>
    /// true/false. Only one should be true. Selects output filetype.
    /// </summary>
    public bool? AvifSelected { get; set; }
    /// <summary>
    /// true/false. Only one should be true. Selects output filetype.
    /// </summary>
    public bool? PngSelected { get; set; }
    /// <summary>
    /// true/false. Only one should be true. Selects output filetype.
    /// </summary>
    public bool? JpegSelected { get; set; }
    /// <summary>
    /// 1/2/3/4 - How much you want to upscale which controls which models will be used
    /// </summary>
    public int? UpscaleScaleFactor { get; set; }

    public static MangaJaNaiUpscalerConfig FromUpscalerProfile(UpscalerProfile profile)
    {
        return new MangaJaNaiUpscalerConfig
        {
            SelectedTabIndex = 0,
            InputFilePath = null,
            InputFolderPath = null,
            OutputFilename = null,
            OutputFolderPath = null,
            OverwriteExistingFiles = true,
            UpscaleImages = true,
            WebpSelected = profile.CompressionFormat == CompressionFormat.Webp,
            AvifSelected = profile.CompressionFormat == CompressionFormat.Avif,
            PngSelected = profile.CompressionFormat == CompressionFormat.Png,
            JpegSelected = profile.CompressionFormat == CompressionFormat.Jpg,
            UpscaleScaleFactor = (int?)profile.ScalingFactor
        };
    }
} 
