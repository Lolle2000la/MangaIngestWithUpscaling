using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterMergeRevertService
{
    /// <summary>
    ///     Reverts a merged chapter back to its original parts
    /// </summary>
    /// <param name="chapter">The merged chapter to revert</param>
    /// <returns>List of restored chapter entities</returns>
    Task<List<Chapter>> RevertMergedChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Checks if a chapter can be reverted (i.e., it has merge information)
    /// </summary>
    /// <param name="chapter">The chapter to check</param>
    /// <returns>True if the chapter can be reverted</returns>
    Task<bool> CanRevertChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets information about the original parts of a merged chapter
    /// </summary>
    /// <param name="chapter">The merged chapter</param>
    /// <returns>Merge information or null if not a merged chapter</returns>
    Task<MergedChapterInfo?> GetMergeInfoAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    );
}
