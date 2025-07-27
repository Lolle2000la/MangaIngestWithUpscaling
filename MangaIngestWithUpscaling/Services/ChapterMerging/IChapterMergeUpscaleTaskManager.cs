using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

public interface IChapterMergeUpscaleTaskManager
{
    /// <summary>
    ///     Handles task queue management when chapters are merged by canceling/removing old tasks
    ///     and optionally queuing a new task for the merged chapter
    /// </summary>
    /// <param name="originalChapters">The original chapters that were merged</param>
    /// <param name="mergeInfo">Information about the merge operation</param>
    /// <param name="library">The library being processed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task HandleUpscaleTaskManagementAsync(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks if chapters can be safely merged considering their upscale status by looking for pending/processing upscale
    ///     tasks. Since we now handle task cancellation and removal, this always returns true but logs information.
    /// </summary>
    /// <param name="chapters">Chapters to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating if merging is compatible</returns>
    Task<UpscaleCompatibilityResult> CheckUpscaleCompatibilityForMergeAsync(
        List<Chapter> chapters,
        CancellationToken cancellationToken = default);
}