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
    /// Compares two CBZ files and returns detailed information about page differences.
    /// Only cbz files are supported.
    /// </summary>
    /// <param name="file1">First CBZ file to compare</param>
    /// <param name="file2">Second CBZ file to compare</param>
    /// <returns>Detailed comparison result showing missing, extra, and common pages</returns>
    PageComparisonResult ComparePages(string? file1, string? file2);
}