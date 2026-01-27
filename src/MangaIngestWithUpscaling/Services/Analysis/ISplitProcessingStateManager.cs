using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;

namespace MangaIngestWithUpscaling.Services.Analysis;

/// <summary>
/// Manages the lifecycle and state transitions of ChapterSplitProcessingState entities.
/// Ensures consistent initialization and versioning across all operations.
/// </summary>
public interface ISplitProcessingStateManager
{
    /// <summary>
    /// Get or create a state for a chapter, ensuring proper initialization.
    /// </summary>
    Task<ChapterSplitProcessingState> GetOrCreateStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark detection as completed (found splits).
    /// Sets Status to Detected and LastProcessedDetectorVersion to the specified version.
    /// </summary>
    Task SetDetectedAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark detection as completed (no splits found).
    /// Sets Status to NoSplitsFound and LastProcessedDetectorVersion to the specified version.
    /// </summary>
    Task SetNoSplitsFoundAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark splits as applied.
    /// Sets Status to Applied and LastAppliedDetectorVersion to the specified version.
    /// Ensures LastProcessedDetectorVersion is initialized if not already set.
    /// </summary>
    Task SetAppliedAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark detection as failed.
    /// Sets Status to Failed.
    /// </summary>
    Task SetFailedAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark detection as processing.
    /// Sets Status to Processing and LastProcessedDetectorVersion to the specified version.
    /// </summary>
    Task SetProcessingAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the current state for a chapter without modifying it.
    /// </summary>
    Task<ChapterSplitProcessingState?> GetStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update the status of an existing state.
    /// </summary>
    Task UpdateStatusAsync(
        int chapterId,
        SplitProcessingStatus newStatus,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reset the state to Pending, clearing all version tracking.
    /// Useful for manually re-triggering detection.
    /// </summary>
    Task ResetToPendingAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete the state for a chapter.
    /// Used when detection needs to be completely reset (e.g., for version upgrades).
    /// </summary>
    Task DeleteStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    );
}
