using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

/// <summary>
/// Represents a chapter found in the ingest path.
/// </summary>
/// <param name="FileName">The file name of the chapter</param>
/// <param name="RelativePath">The relative path to the library root.</param>
/// <param name="StorageType">The type of storage used for the chapter.</param>
/// <param name="SeriesTitle">The title of the series, if any. Requires a ComicInfo.xml-file to be present.</param>
/// <param name="ChapterTitle">The title of the chapter, if any. Requires a ComicInfo.xml-file to be present.</param>
public record FoundChapter(string FileName, string RelativePath, ChapterStorageType StorageType,
    ExtractedMetadata Metadata);

/// <summary>
/// Represents the type of storage for a chapter
/// </summary>
public enum ChapterStorageType
{
    Cbz,
    /// <summary>
    /// Represents a chapter stored in a folder. This means the images are stored as loose files.
    /// </summary>
    Folder
}
