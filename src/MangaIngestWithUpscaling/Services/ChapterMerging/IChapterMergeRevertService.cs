using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterMergeRevertService
{
    /// <summary>
    ///     Reverts a merged chapter back to its original parts
    /// </summary>
    /// <param name="chapter">The merged chapter to revert</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="context">Optional caller-owned context in which the chapter is tracked.</param>
    /// <returns>List of restored chapter entities</returns>
    Task<List<Chapter>> RevertMergedChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default,
        ApplicationDbContext? context = null
    );

    /// <summary>
    ///     Checks if a chapter can be reverted (i.e., it has merge information)
    /// </summary>
    /// <param name="chapter">The chapter to check</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="context">Optional caller-owned context in which the chapter is tracked.</param>
    /// <returns>True if the chapter can be reverted</returns>
    Task<bool> CanRevertChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default,
        ApplicationDbContext? context = null
    );

    /// <summary>
    ///     Gets information about the original parts of a merged chapter
    /// </summary>
    /// <param name="chapter">The merged chapter</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="context">Optional caller-owned context in which the chapter is tracked.</param>
    /// <returns>Merge information or null if not a merged chapter</returns>
    Task<MergedChapterInfo?> GetMergeInfoAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default,
        ApplicationDbContext? context = null
    );
}
