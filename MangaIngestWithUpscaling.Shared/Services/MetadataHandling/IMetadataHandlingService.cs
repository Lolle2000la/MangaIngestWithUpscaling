using System.IO.Compression;

namespace MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

public interface IMetadataHandlingService
{
    ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file);
    void WriteComicInfo(string file, ExtractedMetadata metadata);
    void WriteComicInfo(ZipArchive archive, ExtractedMetadata metadata);

    /// <summary>
    /// Compares two pages to see if they are equal.
    /// Only cbz files are supported.
    /// </summary>
    /// <param name="file1"></param>
    /// <param name="file2"></param>
    /// <returns>True if for every image file in 1 there is a equally named on in 2.</returns>
    bool PagesEqual(string? file1, string? file2);

    /// <summary>
    /// Analyzes the differences between two CBZ files to determine missing and extra pages.
    /// Only cbz files are supported.
    /// </summary>
    /// <param name="originalFile">The original CBZ file</param>
    /// <param name="upscaledFile">The upscaled CBZ file</param>
    /// <returns>A result containing lists of missing and extra page names</returns>
    PageDifferenceResult AnalyzePageDifferences(string? originalFile, string? upscaledFile);
}