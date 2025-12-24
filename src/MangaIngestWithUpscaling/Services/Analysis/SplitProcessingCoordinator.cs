using System.IO.Compression;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using Microsoft.EntityFrameworkCore;
using NetVips;

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
            // Check if the chapter actually needs splitting based on aspect ratio
            var chapter = await db
                .Chapters.Include(c => c.Manga)
                    .ThenInclude(m => m.Library)
                .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

            if (chapter != null && File.Exists(chapter.NotUpscaledFullPath))
            {
                if (!HasImplausiblePages(chapter.NotUpscaledFullPath))
                {
                    // No implausible pages found, so we can skip detection
                    // Update state to prevent re-checking
                    if (state == null)
                    {
                        state = new ChapterSplitProcessingState { ChapterId = chapterId };
                        db.ChapterSplitProcessingStates.Add(state);
                    }
                    else
                    {
                        db.ChapterSplitProcessingStates.Attach(state);
                        db.Entry(state).State = EntityState.Modified;
                    }

                    state.LastProcessedDetectorVersion =
                        SplitDetectionService.CURRENT_DETECTOR_VERSION;
                    state.Status = SplitProcessingStatus.Detected; // Treated as detected with 0 splits
                    state.ModifiedAt = DateTime.UtcNow;

                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private bool HasImplausiblePages(string chapterPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(chapterPath);
            foreach (var entry in archive.Entries)
            {
                if (
                    !ImageConstants.SupportedImageExtensions.Contains(
                        Path.GetExtension(entry.Name).ToLower()
                    )
                )
                    continue;

                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                // Use sequential access for better performance with streams
                using var image = Image.NewFromStream(ms, access: Enums.Access.Sequential);

                // Check aspect ratio: Height / Width > 2.4
                // This suggests a vertical strip (webtoon) that likely needs splitting
                if ((double)image.Height / image.Width > 2.4)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check aspect ratio for {Path}", chapterPath);
            // If check fails, assume we should process to be safe
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
