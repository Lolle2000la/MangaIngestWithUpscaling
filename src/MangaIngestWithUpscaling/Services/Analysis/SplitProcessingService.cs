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
    IFileSystem fileSystem,
    ISplitProcessingStateManager stateManager
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
        var resultList = results.ToList();

        // Check for errors in the results
        var failedResults = resultList.Where(r => !string.IsNullOrWhiteSpace(r.Error)).ToList();
        if (failedResults.Count > 0)
        {
            await stateManager.SetFailedAsync(chapterId, null, cancellationToken);

            var uniqueErrors = failedResults
                .Select(r => $"{Path.GetFileName(r.ImagePath)}: {r.Error}")
                .Distinct();
            var errorMessage = string.Join("; ", uniqueErrors);

            logger.LogError(
                "Split detection failed for chapter {ChapterId}. Errors: {Errors}",
                chapterId,
                errorMessage
            );
            return;
        }

        // Remove existing findings for this chapter to ensure clean state for this version
        var existingFindings = dbContext.StripSplitFindings.Where(f => f.ChapterId == chapterId);
        dbContext.StripSplitFindings.RemoveRange(existingFindings);

        // Only save findings that actually have splits detected
        var resultsWithSplits = resultList.Where(r => r.Splits.Count > 0).ToList();

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

        await dbContext.SaveChangesAsync(cancellationToken);

        // Use state manager to set appropriate status
        if (resultsWithSplits.Count == 0)
        {
            await stateManager.SetNoSplitsFoundAsync(
                chapterId,
                detectorVersion,
                null,
                cancellationToken
            );
        }
        else
        {
            await stateManager.SetDetectedAsync(
                chapterId,
                detectorVersion,
                null,
                cancellationToken
            );
        }

        // Get the status that was just set for logging
        var state = await stateManager.GetStateAsync(chapterId, null, cancellationToken);
        var statusForLogging = state?.Status.ToString() ?? "Unknown";

        logger.LogInformation(
            "Processed split detection results for chapter {ChapterId}, version {Version}. Found splits for {Count} images. Status set to {Status}.",
            chapterId,
            detectorVersion,
            resultsWithSplits.Count,
            statusForLogging
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
                await stateManager.SetProcessingAsync(
                    chapterId,
                    detectorVersion,
                    null,
                    cancellationToken
                );
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
