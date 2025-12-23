using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

/// <summary>
///     Result of processing chapters for merging
/// </summary>
/// <param name="ProcessedChapters">The final list of chapters after merging (merged chapters + unmerged chapters)</param>
/// <param name="MergeInformation">Information about merges performed for database tracking</param>
public record ChapterMergeResult(
    List<FoundChapter> ProcessedChapters,
    List<MergeInfo> MergeInformation
);

/// <summary>
///     Information about a merge operation
/// </summary>
/// <param name="MergedChapter">The resulting merged chapter</param>
/// <param name="OriginalParts">The original chapter parts that were merged</param>
/// <param name="BaseChapterNumber">The base chapter number</param>
public record MergeInfo(
    FoundChapter MergedChapter,
    List<OriginalChapterPart> OriginalParts,
    string BaseChapterNumber
);

public interface IChapterPartMerger
{
    /// <summary>
    ///     Processes a list of chapters and performs merging where appropriate
    /// </summary>
    /// <param name="chapters">List of chapters to process</param>
    /// <param name="basePath">Base path where chapters are located</param>
    /// <param name="outputPath">Path where merged chapters should be created</param>
    /// <param name="seriesTitle">The series title to use in metadata</param>
    /// <param name="existingChapterNumbers">Set of all existing chapter numbers to determine latest chapters</param>
    /// <param name="getActualFilePath">
    ///     Function to get the actual file path for a chapter (to handle renamed vs original
    ///     paths)
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing processed chapters and merge information</returns>
    Task<ChapterMergeResult> ProcessChapterMergingAsync(
        List<FoundChapter> chapters,
        string basePath,
        string outputPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        Func<FoundChapter, string>? getActualFilePath = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Processes existing chapters from the database to identify and merge chapter parts.
    /// This method analyzes chapters that are already stored in the library to find
    /// groups of chapter parts that can be merged together (e.g., Chapter 5.1, 5.2, 5.3).
    /// </summary>
    /// <param name="existingChapters">List of existing chapters from database</param>
    /// <param name="libraryPath">Path to the library where chapters are stored</param>
    /// <param name="seriesTitle">The series title</param>
    /// <param name="existingChapterNumbers">Set of all existing chapter numbers</param>
    /// <param name="excludeMergedChapterIds">Set of chapter IDs that are already merged and should be skipped</param>
    /// <param name="existingMergedBaseNumbers">Set of base numbers for existing merged chapters</param>
    /// <param name="existingMergedParts">Dictionary mapping base numbers to lists of already-merged chapter numbers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing merge information for database updates</returns>
    Task<ChapterMergeResult> ProcessExistingChapterPartsAsync(
        List<Chapter> existingChapters,
        string libraryPath,
        string seriesTitle,
        HashSet<string> existingChapterNumbers,
        HashSet<int> excludeMergedChapterIds,
        HashSet<string> existingMergedBaseNumbers,
        Dictionary<string, List<string>> existingMergedParts,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Groups chapters by their base number and identifies chapter parts that should be merged
    /// </summary>
    /// <param name="chapters">List of chapters to analyze</param>
    /// <param name="isLastChapter">Function to determine if a chapter group is the latest chapter</param>
    /// <returns>Dictionary where key is the base chapter number and value is list of chapter parts to merge</returns>
    Dictionary<string, List<FoundChapter>> GroupChapterPartsForMerging(
        IEnumerable<FoundChapter> chapters,
        Func<string, bool> isLastChapter
    );

    /// <summary>
    /// Groups chapters that can be added to existing merged chapters.
    /// This handles individual chapters (like 2.3) that can be added to existing merged chapters (like merged chapter 2).
    /// Only chapters that would form a consecutive sequence with the already-merged parts are included.
    /// </summary>
    /// <param name="chapters">Chapters to analyze</param>
    /// <param name="existingMergedBaseNumbers">Base numbers of existing merged chapters</param>
    /// <param name="existingMergedParts">Dictionary mapping base numbers to lists of already-merged chapter numbers (e.g., "1" -> ["1.1", "1.2"])</param>
    /// <param name="isLastChapter">Function to determine if a chapter group is the latest chapter</param>
    /// <returns>Dictionary where key is base chapter number and value is list of chapters to add to existing merged chapter</returns>
    Dictionary<string, List<FoundChapter>> GroupChaptersForAdditionToExistingMerged(
        IEnumerable<FoundChapter> chapters,
        HashSet<string> existingMergedBaseNumbers,
        Dictionary<string, List<string>> existingMergedParts,
        Func<string, bool> isLastChapter
    );

    /// <summary>
    ///     Merges multiple chapter parts into a single CBZ file
    /// </summary>
    /// <param name="chapterParts">List of chapter parts to merge</param>
    /// <param name="basePath">Base path where chapters are located</param>
    /// <param name="outputPath">Path where the merged chapter should be created</param>
    /// <param name="baseChapterNumber">The base chapter number for the merged chapter</param>
    /// <param name="targetMetadata">The target metadata for the merged chapter</param>
    /// <param name="getActualFilePath">Function to get the actual file path for a chapter (to handle renamed vs original paths)</param>
    /// <returns>Information about the merged chapter and original parts for reverting</returns>
    Task<(
        FoundChapter mergedChapter,
        List<OriginalChapterPart> originalParts
    )> MergeChapterPartsAsync(
        List<FoundChapter> chapterParts,
        string basePath,
        string outputPath,
        string baseChapterNumber,
        ExtractedMetadata targetMetadata,
        Func<FoundChapter, string>? getActualFilePath = null,
        CancellationToken cancellationToken = default
    );

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
        CancellationToken cancellationToken = default
    );
}
