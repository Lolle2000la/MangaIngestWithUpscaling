using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Services.Analysis;

public interface ISplitProcessingCoordinator
{
    /// <summary>
    /// Checks if split detection should be run for the given chapter based on the library's detection mode
    /// and the current state of the chapter's processing.
    /// </summary>
    /// <param name="chapterId">The ID of the chapter to check.</param>
    /// <param name="mode">The strip detection mode configured for the library/series.</param>
    /// <param name="context">Optional DbContext to use for the check (useful for parallel operations).</param>
    /// <returns>True if detection is needed, false otherwise.</returns>
    Task<bool> ShouldProcessAsync(
        int chapterId,
        StripDetectionMode mode,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Enqueues a split detection task for the given chapter.
    /// </summary>
    /// <param name="chapterId">The ID of the chapter.</param>
    Task EnqueueDetectionAsync(int chapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues split detection tasks for multiple chapters.
    /// </summary>
    /// <param name="chapterIds">The IDs of the chapters.</param>
    Task EnqueueDetectionBatchAsync(
        IEnumerable<int> chapterIds,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks if the chapter has plausible pages for splitting (aspect ratio check) and enqueues detection if so.
    /// If not plausible, updates the state to Detected (with 0 splits) and skips the expensive task.
    /// </summary>
    /// <returns>True if a task was enqueued, false if it was skipped (completed immediately).</returns>
    Task<bool> EnqueueDetectionIfPlausibleAsync(
        int chapterId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Handles the completion of split application (either local or remote).
    /// Updates the processing state, notifies changes, and schedules subsequent upscale/repair tasks if needed.
    /// </summary>
    Task OnSplitsAppliedAsync(
        int chapterId,
        int detectorVersion,
        CancellationToken cancellationToken = default
    );
}
