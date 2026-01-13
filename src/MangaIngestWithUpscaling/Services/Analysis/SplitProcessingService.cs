using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitProcessingService(
    ApplicationDbContext dbContext,
    ILogger<SplitProcessingService> logger,
    ITaskQueue taskQueue,
    IFileSystem fileSystem
) : ISplitProcessingService
{
    public async Task ProcessDetectionResultAsync(
        int chapterId,
        SplitDetectionResult result,
        int detectorVersion,
        CancellationToken cancellationToken
    )
    {
        // This overload is for single image processing if needed, but we prefer batch.
        // For now, let's just wrap it in a list.
        await ProcessDetectionResultsAsync(chapterId, [result], detectorVersion, cancellationToken);
    }

    public async Task ProcessDetectionResultsAsync(
        int chapterId,
        IEnumerable<SplitDetectionResult> results,
        int detectorVersion,
        CancellationToken cancellationToken
    )
    {
        var state = await dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapterId,
            cancellationToken
        );

        if (state == null)
        {
            state = new ChapterSplitProcessingState
            {
                ChapterId = chapterId,
                Status = SplitProcessingStatus.Pending,
            };
            dbContext.ChapterSplitProcessingStates.Add(state);
        }

        // Remove existing findings for this chapter to ensure clean state for this version
        var existingFindings = dbContext.StripSplitFindings.Where(f => f.ChapterId == chapterId);
        dbContext.StripSplitFindings.RemoveRange(existingFindings);

        // Only save findings that actually have splits detected
        var resultsWithSplits = results.Where(r => r.Splits.Count > 0).ToList();

        foreach (var res in resultsWithSplits)
        {
            // We use the file name without extension as the identity
            var pageFileName = Path.GetFileNameWithoutExtension(res.ImagePath);

            var finding = new StripSplitFinding
            {
                ChapterId = chapterId,
                PageFileName = pageFileName,
                SplitJson = JsonSerializer.Serialize(res),
                DetectorVersion = detectorVersion,
                CreatedAt = DateTime.UtcNow,
            };
            dbContext.StripSplitFindings.Add(finding);
        }

        state.LastProcessedDetectorVersion = detectorVersion;
        state.ModifiedAt = DateTime.UtcNow;

        if (resultsWithSplits.Count == 0)
        {
            state.Status = SplitProcessingStatus.NoSplitsFound;
        }
        else
        {
            state.Status = SplitProcessingStatus.Detected;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Processed split detection results for chapter {ChapterId}, version {Version}. Found splits for {Count} images. Status set to {Status}.",
            chapterId,
            detectorVersion,
            resultsWithSplits.Count,
            state.Status
        );

        // Check if we should auto-apply (only if there are actual splits to apply)
        if (resultsWithSplits.Count > 0)
        {
            var chapter = await dbContext
                .Chapters.Include(c => c.Manga)
                    .ThenInclude(m => m.Library)
                        .ThenInclude(l => l.UpscalerProfile)
                .Include(c => c.Manga)
                    .ThenInclude(m => m.UpscalerProfilePreference)
                .Include(c => c.UpscalerProfile)
                .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

            if (chapter?.Manga?.Library?.StripDetectionMode == StripDetectionMode.DetectAndApply)
            {
                logger.LogInformation(
                    "Auto-applying splits for chapter {ChapterId} based on library settings.",
                    chapterId
                );
                await taskQueue.EnqueueAsync(new ApplySplitsTask(chapter, detectorVersion));

                // Update status to Processing immediately to reflect the queued task
                state.Status = SplitProcessingStatus.Processing;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (
                chapter != null
                && chapter.Manga?.Library?.StripDetectionMode == StripDetectionMode.DetectOnly
                && chapter.Manga.Library.UpscaleOnIngest
                && chapter.Manga.ShouldUpscale != false
                && chapter.Manga.Library.UpscalerProfileId != null
            )
            {
                if (
                    chapter.IsUpscaled
                    || (
                        chapter.UpscaledFullPath != null
                        && fileSystem.FileExists(chapter.UpscaledFullPath)
                    )
                )
                {
                    await taskQueue.EnqueueAsync(new RepairUpscaleTask(chapter));
                }
                else
                {
                    await taskQueue.EnqueueAsync(new UpscaleTask(chapter));
                }
            }
        }
        else if (results.Any())
        {
            // No splits detected, but we still need to proceed with upscaling if configured
            var chapter = await dbContext
                .Chapters.Include(c => c.Manga)
                    .ThenInclude(m => m.Library)
                        .ThenInclude(l => l.UpscalerProfile)
                .Include(c => c.Manga)
                    .ThenInclude(m => m.UpscalerProfilePreference)
                .Include(c => c.UpscalerProfile)
                .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

            if (
                chapter != null
                && chapter.Manga?.Library?.UpscaleOnIngest == true
                && chapter.Manga.ShouldUpscale != false
                && chapter.Manga.Library.UpscalerProfileId != null
            )
            {
                if (
                    chapter.IsUpscaled
                    || (
                        chapter.UpscaledFullPath != null
                        && fileSystem.FileExists(chapter.UpscaledFullPath)
                    )
                )
                {
                    await taskQueue.EnqueueAsync(new RepairUpscaleTask(chapter));
                }
                else
                {
                    await taskQueue.EnqueueAsync(new UpscaleTask(chapter));
                }
            }
        }
    }

    public async Task QueueSplitDetectionAsync(int chapterId)
    {
        var chapter = await dbContext
            .Chapters.Include(c => c.Manga)
            .FirstOrDefaultAsync(c => c.Id == chapterId);

        if (chapter != null)
        {
            await taskQueue.EnqueueAsync(
                new DetectSplitCandidatesTask(
                    chapter,
                    SplitDetectionService.CURRENT_DETECTOR_VERSION
                )
            );
        }
        else
        {
            await taskQueue.EnqueueAsync(
                new DetectSplitCandidatesTask(
                    chapterId,
                    SplitDetectionService.CURRENT_DETECTOR_VERSION
                )
            );
        }
    }

    public async Task<List<StripSplitFinding>> GetSplitFindingsAsync(int chapterId)
    {
        return await dbContext
            .StripSplitFindings.Where(f => f.ChapterId == chapterId)
            .OrderBy(f => f.PageFileName)
            .ToListAsync();
    }
}
