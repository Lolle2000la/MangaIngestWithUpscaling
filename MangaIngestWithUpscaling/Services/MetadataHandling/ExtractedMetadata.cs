namespace MangaIngestWithUpscaling.Services.MetadataHandling;

/// <summary>
/// Represents metadata extracted from a ComicInfo.xml file.
/// </summary>
/// <param name="Series">The name of the series this chapter belongs to.</param>
/// <param name="ChapterTitle">The title of the chapter, usually something like "Chapter 1" or "第１話".</param>
public record ExtractedMetadata(string Series, string? ChapterTitle, string? Number)
{

    /// <summary>
    /// Checks if the metadata is correct and corrects it if possible.
    /// </summary>
    /// <returns>The corrected metadata.</returns>
    public ExtractedMetadata CheckAndCorrect()
    {
        throw new NotImplementedException();
    }
};
