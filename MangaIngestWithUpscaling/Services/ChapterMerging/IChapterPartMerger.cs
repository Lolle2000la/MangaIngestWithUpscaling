using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterPartMerger
{
    /// <summary>
    ///     Groups chapters by their base number and identifies chapter parts that should be merged
    /// </summary>
    /// <param name="chapters">List of chapters to analyze</param>
    /// <param name="isLastChapter">Function to determine if a chapter group is the latest chapter</param>
    /// <returns>Dictionary where key is the base chapter number and value is list of chapter parts to merge</returns>
    Dictionary<string, List<FoundChapter>> GroupChapterPartsForMerging(
        IEnumerable<FoundChapter> chapters,
        Func<string, bool> isLastChapter);

    /// <summary>
    ///     Merges multiple chapter parts into a single CBZ file
    /// </summary>
    /// <param name="chapterParts">List of chapter parts to merge</param>
    /// <param name="basePath">Base path where chapters are located</param>
    /// <param name="outputPath">Path where the merged chapter should be created</param>
    /// <param name="baseChapterNumber">The base chapter number for the merged chapter</param>
    /// <returns>Information about the merged chapter and original parts for reverting</returns>
    Task<(FoundChapter mergedChapter, List<OriginalChapterPart> originalParts)> MergeChapterPartsAsync(
        List<FoundChapter> chapterParts,
        string basePath,
        string outputPath,
        string baseChapterNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restores original chapter parts from a merged chapter
    /// </summary>
    /// <param name="mergedChapterPath">Path to the merged chapter CBZ</param>
    /// <param name="originalParts">Information about original parts</param>
    /// <param name="outputDirectory">Directory where original parts should be restored</param>
    /// <returns>List of restored chapter files</returns>
    Task<List<FoundChapter>> RestoreChapterPartsAsync(
        string mergedChapterPath,
        List<OriginalChapterPart> originalParts,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}