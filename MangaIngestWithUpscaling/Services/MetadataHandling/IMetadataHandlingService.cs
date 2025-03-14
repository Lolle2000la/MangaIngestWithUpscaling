using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public interface IMetadataHandlingService
{
    ExtractedMetadata GetSeriesAndTitleFromComicInfo(string file);
    void WriteComicInfo(string file, ExtractedMetadata metadata);
    /// <summary>
    /// Compares two pages to see if they are equal.
    /// Only cbz files are supported.
    /// </summary>
    /// <param name="file1"></param>
    /// <param name="file2"></param>
    /// <returns>True if for every image file in 1 there is a equally named on in 2.</returns>
    bool PagesEqual(string? file1, string? file2);
}
