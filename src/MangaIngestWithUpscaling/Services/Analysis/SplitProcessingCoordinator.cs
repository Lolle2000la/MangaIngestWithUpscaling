using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitProcessingCoordinator(
    ApplicationDbContext dbContext,
    ITaskQueue taskQueue,
    ILogger<SplitProcessingCoordinator> logger
) : ISplitProcessingCoordinator
{
    public async Task<bool> ShouldProcessAsync(
        int chapterId,
        StripDetectionMode mode,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        if (mode == StripDetectionMode.None)
        {
            return false;
        }

        var db = context ?? dbContext;
        var state = await db
            .ChapterSplitProcessingStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ChapterId == chapterId, cancellationToken);

        // Process if no state exists or if the detector version is outdated
        if (
            state == null
            || state.LastProcessedDetectorVersion < SplitDetectionService.CURRENT_DETECTOR_VERSION
        )
        {
            return true;
        }

        return false;
    }

    public async Task EnqueueDetectionAsync(
        int chapterId,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation(
            "Enqueuing split detection for chapter {ChapterId} (Version {Version})",
            chapterId,
            SplitDetectionService.CURRENT_DETECTOR_VERSION
        );

        var task = new DetectSplitCandidatesTask(
            chapterId,
            SplitDetectionService.CURRENT_DETECTOR_VERSION
        );
        await taskQueue.EnqueueAsync(task);
    }

    public async Task EnqueueDetectionBatchAsync(
        IEnumerable<int> chapterIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = chapterIds.ToList();
        if (ids.Count == 0)
            return;

        logger.LogInformation(
            "Enqueuing split detection for {Count} chapters (Version {Version})",
            ids.Count,
            SplitDetectionService.CURRENT_DETECTOR_VERSION
        );

        foreach (var id in ids)
        {
            var task = new DetectSplitCandidatesTask(
                id,
                SplitDetectionService.CURRENT_DETECTOR_VERSION
            );
            await taskQueue.EnqueueAsync(task);
        }
    }
}
