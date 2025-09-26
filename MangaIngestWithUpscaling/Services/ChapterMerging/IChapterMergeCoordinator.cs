using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterMergeCoordinator
{
    /// <summary>
    /// Processes existing chapters in a manga series to identify and merge chapter parts.
    /// This is typically called after new chapters are ingested to check if any previously
    /// separate chapter parts can now be merged together.
    /// </summary>
    /// <param name="manga">The manga whose chapters should be processed for merging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ProcessExistingChapterPartsForMergingAsync(
        Manga manga,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Updates the database records after a successful chapter merge operation.
    /// This consolidates multiple chapter records into a single merged chapter record
    /// and creates tracking information for potential future reversion.
    /// </summary>
    /// <param name="mergeInfo">Information about the merge operation that was performed</param>
    /// <param name="originalChapters">The original chapter records that were merged</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateDatabaseForMergeAsync(
        MergeInfo mergeInfo,
        List<Chapter> originalChapters,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Performs a complete chapter merging operation for the specified chapters,
    /// including both standard and upscaled versions if applicable.
    /// </summary>
    /// <param name="chapters">The chapters to merge together</param>
    /// <param name="library">The library containing the chapters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MergeChaptersAsync(
        List<Chapter> chapters,
        Library library,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Performs manual chapter merging for selected chapters, with optional latest chapter handling.
    /// This mirrors the automatic merging behavior but allows manual selection of chapters.
    /// </summary>
    /// <param name="selectedChapters">The chapters selected for manual merging</param>
    /// <param name="includeLatestChapters">Whether to include latest chapters in merging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Information about completed merge operations</returns>
    Task<List<MergeInfo>> MergeSelectedChaptersAsync(
        List<Chapter> selectedChapters,
        bool includeLatestChapters = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Validates which of the selected chapters can be merged and groups them appropriately.
    /// </summary>
    /// <param name="selectedChapters">The chapters to validate for merging</param>
    /// <param name="includeLatestChapters">Whether to include latest chapters in the validation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of merge groups, where key is base chapter number and value is list of chapters to merge</returns>
    Task<Dictionary<string, List<Chapter>>> GetValidMergeGroupsAsync(
        List<Chapter> selectedChapters,
        bool includeLatestChapters = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks if a chapter can be added to an existing merged chapter.
    /// </summary>
    /// <param name="chapter">The chapter to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the chapter can be added to an existing merged chapter</returns>
    Task<bool> CanChapterBeAddedToExistingMergedAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets all possible merge actions for the given chapters, including both new merges and additions to existing merged chapters.
    /// </summary>
    /// <param name="chapters">The chapters to analyze</param>
    /// <param name="includeLatestChapters">Whether to include latest chapters in the analysis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Information about all possible merge actions</returns>
    Task<MergeActionInfo> GetPossibleMergeActionsAsync(
        List<Chapter> chapters,
        bool includeLatestChapters = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks if a chapter part has already been merged into another chapter.
    /// </summary>
    /// <param name="chapterFileName">The filename of the chapter to check</param>
    /// <param name="manga">The manga containing the chapter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the chapter part has already been merged</returns>
    Task<bool> IsChapterPartAlreadyMergedAsync(
        string chapterFileName,
        Manga manga,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Information about possible merge actions for chapters
/// </summary>
public class MergeActionInfo
{
    /// <summary>
    /// Chapters that can form new merge groups
    /// </summary>
    public Dictionary<string, List<Chapter>> NewMergeGroups { get; set; } = new();

    /// <summary>
    /// Chapters that can be added to existing merged chapters
    /// </summary>
    public Dictionary<string, List<Chapter>> AdditionsToExistingMerged { get; set; } = new();

    /// <summary>
    /// Whether any merge actions are possible
    /// </summary>
    public bool HasAnyMergePossibilities => NewMergeGroups.Any() || AdditionsToExistingMerged.Any();
}
