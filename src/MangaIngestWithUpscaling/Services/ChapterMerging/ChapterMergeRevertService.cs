using System.IO.Compression;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeRevertService(
    ApplicationDbContext dbContext,
    IChapterPartMerger chapterPartMerger,
    IChapterChangedNotifier chapterChangedNotifier,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    ITaskQueue taskQueue,
    IStringLocalizer<ChapterMergeRevertService> localizer,
    ILogger<ChapterMergeRevertService> logger
) : IChapterMergeRevertService
{
    public async Task<List<Chapter>> RevertMergedChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    )
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        if (mergeInfo == null)
        {
            throw new InvalidOperationException(
                localizer["Error_NotAMergedChapter", chapter.FileName]
            );
        }

        // Load chapter dependencies
        if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
        {
            await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(chapter.Manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext
                .Entry(chapter.Manga)
                .Reference(m => m.Library)
                .LoadAsync(cancellationToken);
        }

        Library library = chapter.Manga.Library;
        string mergedChapterPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            chapter.RelativePath
        );

        if (!File.Exists(mergedChapterPath))
        {
            throw new FileNotFoundException(
                localizer["Error_MergedChapterFileNotFound", mergedChapterPath]
            );
        }

        // If a partial upscaling repair task was scheduled for this merged chapter, cancel it now to avoid wasted work
        await CancelPendingRepairTaskForChapterAsync(chapter.Id, cancellationToken);

        try
        {
            // Get original parts information from strongly typed property
            List<OriginalChapterPart>? originalParts = mergeInfo.OriginalParts;
            if (originalParts == null || !originalParts.Any())
            {
                throw new InvalidOperationException(localizer["Error_InvalidMergeInfo"]);
            }

            logger.LogInformation(
                "Reverting merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName,
                originalParts.Count
            );

            // Create output directory for the restored parts
            string seriesDirectory = Path.Combine(
                library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(chapter.Manga.PrimaryTitle!)
            );

            // Restore the original parts
            List<FoundChapter> restoredChapters = await chapterPartMerger.RestoreChapterPartsAsync(
                mergedChapterPath,
                originalParts,
                seriesDirectory,
                cancellationToken
            );

            // Clean up any existing chapters with the same filenames to avoid constraint violations
            await CleanupExistingChaptersAsync(
                restoredChapters,
                chapter.MangaId,
                chapter.IsUpscaled,
                cancellationToken
            );

            // Create Chapter entities for the restored parts
            var restoredChapterEntities = new List<Chapter>();

            foreach (FoundChapter restoredChapter in restoredChapters)
            {
                // Create chapter entity
                var chapterEntity = new Chapter
                {
                    FileName = restoredChapter.FileName,
                    Manga = chapter.Manga,
                    MangaId = chapter.MangaId,
                    RelativePath = Path.GetRelativePath(
                        library.NotUpscaledLibraryPath,
                        Path.Combine(seriesDirectory, restoredChapter.FileName)
                    ),
                    IsUpscaled = chapter.IsUpscaled,
                    UpscalerProfileId = chapter.UpscalerProfileId,
                    UpscalerProfile = chapter.UpscalerProfile,
                };

                dbContext.Chapters.Add(chapterEntity);
                restoredChapterEntities.Add(chapterEntity);

                // Notify about the new chapter
                _ = chapterChangedNotifier.Notify(chapterEntity, false);
            }

            // Remove the merged chapter (but keep merge info for upscaled restoration)
            dbContext.Chapters.Remove(chapter);

            // Delete the merged chapter file
            File.Delete(mergedChapterPath);

            // Clean up empty directories
            FileSystemHelpers.DeleteIfEmpty(Path.GetDirectoryName(mergedChapterPath)!, logger);

            // Save regular chapters first
            await dbContext.SaveChangesAsync(cancellationToken);

            // Now create upscaled versions if needed
            if (chapter.IsUpscaled && chapter.UpscalerProfile != null)
            {
                await CreateUpscaledRestoredChaptersAsync(
                    chapter,
                    library,
                    mergeInfo,
                    restoredChapterEntities,
                    cancellationToken
                );
            }

            logger.LogInformation(
                "Successfully reverted merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName,
                originalParts.Count
            );

            return restoredChapterEntities;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revert merged chapter {ChapterFile}", chapter.FileName);
            throw;
        }
    }

    public async Task<bool> CanRevertChapterAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    )
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        return mergeInfo != null;
    }

    public async Task<MergedChapterInfo?> GetMergeInfoAsync(
        Chapter chapter,
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext.MergedChapterInfos.FirstOrDefaultAsync(
            m => m.ChapterId == chapter.Id,
            cancellationToken
        );
    }

    private async Task CreateUpscaledRestoredChaptersAsync(
        Chapter originalChapter,
        Library library,
        MergedChapterInfo? mergeInfo,
        List<Chapter> restoredChapters,
        CancellationToken cancellationToken
    )
    {
        // Get the path to the upscaled merged chapter file
        string upscaledMergedChapterPath = Path.Combine(
            library.UpscaledLibraryPath!,
            originalChapter.RelativePath
        );

        if (!File.Exists(upscaledMergedChapterPath))
        {
            logger.LogWarning(
                "Upscaled merged chapter file not found: {UpscaledPath}, will check for pending repair tasks and individual parts",
                upscaledMergedChapterPath
            );

            // Corner case 2: Check if there's a pending RepairUpscaleTask that hasn't completed yet
            await HandleMissingUpscaledChapterWithPendingRepairAsync(
                originalChapter,
                library,
                mergeInfo,
                restoredChapters,
                cancellationToken
            );
            return;
        }

        // Create upscaled directory structure for restored parts
        string upscaledSeriesDirectory = Path.Combine(
            library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(originalChapter.Manga.PrimaryTitle!)
        );
        Directory.CreateDirectory(upscaledSeriesDirectory);

        // Get the merge info to know the original parts structure
        if (mergeInfo?.OriginalParts == null)
        {
            logger.LogWarning("No merge info found for upscaled chapter restoration, skipping");
            return;
        }

        // Corner case 1: Check if this was a partial merge that was later completed
        // We need to verify that the upscaled file contains all expected pages
        await HandlePotentialPartialMergeRevertAsync(
            originalChapter,
            library,
            mergeInfo,
            upscaledMergedChapterPath,
            upscaledSeriesDirectory,
            restoredChapters,
            cancellationToken
        );
    }

    private async Task HandleMissingUpscaledChapterWithPendingRepairAsync(
        Chapter originalChapter,
        Library library,
        MergedChapterInfo? mergeInfo,
        List<Chapter> restoredChapters,
        CancellationToken cancellationToken
    )
    {
        if (mergeInfo?.OriginalParts == null)
        {
            logger.LogWarning("No merge info available for missing upscaled chapter restoration");
            return;
        }

        // Check if there are any individual upscaled parts that we can restore
        string upscaledSeriesDirectory = Path.Combine(
            library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(originalChapter.Manga.PrimaryTitle!)
        );

        var availableUpscaledParts = new List<OriginalChapterPart>();
        var missingUpscaledParts = new List<OriginalChapterPart>();

        foreach (var originalPart in mergeInfo.OriginalParts)
        {
            string upscaledPartPath = Path.Combine(upscaledSeriesDirectory, originalPart.FileName);
            if (File.Exists(upscaledPartPath))
            {
                availableUpscaledParts.Add(originalPart);
            }
            else
            {
                missingUpscaledParts.Add(originalPart);
            }
        }

        if (availableUpscaledParts.Any())
        {
            logger.LogInformation(
                "Found {AvailableCount} upscaled parts available for restoration out of {TotalCount} total parts. "
                    + "Missing parts: {MissingParts}",
                availableUpscaledParts.Count,
                mergeInfo.OriginalParts.Count,
                string.Join(", ", missingUpscaledParts.Select(p => p.FileName))
            );

            // Restore only the available upscaled parts
            foreach (var availablePart in availableUpscaledParts)
            {
                string upscaledPartPath = Path.Combine(
                    upscaledSeriesDirectory,
                    availablePart.FileName
                );

                // Add upscaler.json to the existing upscaled part
                await using ZipArchive archive = await ZipFile.OpenAsync(
                    upscaledPartPath,
                    ZipArchiveMode.Update,
                    cancellationToken
                );
                await upscalerJsonHandlingService.WriteUpscalerJsonAsync(
                    archive,
                    originalChapter.UpscalerProfile!,
                    cancellationToken
                );
            }

            // Schedule upscale tasks for the missing parts
            if (missingUpscaledParts.Any())
            {
                await ScheduleUpscaleTasksForMissingPartsAsync(
                    missingUpscaledParts,
                    originalChapter,
                    library,
                    restoredChapters,
                    cancellationToken
                );
            }
        }
        else
        {
            logger.LogInformation(
                "No upscaled parts found for restoration. All parts will need to be upscaled from scratch."
            );

            // Schedule upscale tasks for all parts since none exist
            await ScheduleUpscaleTasksForMissingPartsAsync(
                mergeInfo.OriginalParts.ToList(),
                originalChapter,
                library,
                restoredChapters,
                cancellationToken
            );
        }
    }

    private async Task HandlePotentialPartialMergeRevertAsync(
        Chapter originalChapter,
        Library library,
        MergedChapterInfo mergeInfo,
        string upscaledMergedChapterPath,
        string upscaledSeriesDirectory,
        List<Chapter> restoredChapters,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Restore the upscaled chapter parts using the same logic as regular restoration
            List<FoundChapter> upscaledRestoredChapters =
                await chapterPartMerger.RestoreChapterPartsAsync(
                    upscaledMergedChapterPath,
                    mergeInfo.OriginalParts,
                    upscaledSeriesDirectory,
                    cancellationToken
                );

            // Verify that all expected parts were restored
            var restoredFileNames = upscaledRestoredChapters.Select(c => c.FileName).ToHashSet();
            var expectedFileNames = mergeInfo.OriginalParts.Select(p => p.FileName).ToHashSet();
            var missingFromRestoration = expectedFileNames.Except(restoredFileNames).ToList();

            if (missingFromRestoration.Any())
            {
                logger.LogWarning(
                    "Restored upscaled chapter is missing {MissingCount} parts: {MissingParts}. "
                        + "This suggests the merged file was from a partial merge that wasn't completed by RepairUpscaleTask.",
                    missingFromRestoration.Count,
                    string.Join(", ", missingFromRestoration)
                );

                // Corner case 1: Schedule upscale tasks for the missing parts
                List<OriginalChapterPart> missingParts = mergeInfo
                    .OriginalParts.Where(p => missingFromRestoration.Contains(p.FileName))
                    .ToList();
                await ScheduleUpscaleTasksForMissingPartsAsync(
                    missingParts,
                    originalChapter,
                    library,
                    restoredChapters,
                    cancellationToken
                );
            }

            // Add upscaler.json to each restored upscaled part
            foreach (FoundChapter upscaledRestoredChapter in upscaledRestoredChapters)
            {
                string upscaledChapterPath = Path.Combine(
                    upscaledSeriesDirectory,
                    upscaledRestoredChapter.FileName
                );

                // Add upscaler.json to the upscaled chapter
                await using ZipArchive archive = await ZipFile.OpenAsync(
                    upscaledChapterPath,
                    ZipArchiveMode.Update,
                    cancellationToken
                );
                await upscalerJsonHandlingService.WriteUpscalerJsonAsync(
                    archive,
                    originalChapter.UpscalerProfile!,
                    cancellationToken
                );
            }

            // Delete the upscaled merged chapter file
            File.Delete(upscaledMergedChapterPath);

            logger.LogInformation(
                "Restored {Count} upscaled chapter parts from {MergedFile}",
                upscaledRestoredChapters.Count,
                Path.GetFileName(upscaledMergedChapterPath)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to restore upscaled chapter parts from {MergedFile}. "
                    + "Will attempt to schedule individual upscale tasks for all parts.",
                Path.GetFileName(upscaledMergedChapterPath)
            );

            // Fallback: Schedule upscale tasks for all original parts
            await ScheduleUpscaleTasksForMissingPartsAsync(
                mergeInfo.OriginalParts.ToList(),
                originalChapter,
                library,
                restoredChapters,
                cancellationToken
            );
        }
    }

    private async Task ScheduleUpscaleTasksForMissingPartsAsync(
        List<OriginalChapterPart> missingParts,
        Chapter originalChapter,
        Library library,
        List<Chapter> restoredChapters,
        CancellationToken cancellationToken
    )
    {
        if (!missingParts.Any() || originalChapter.UpscalerProfile == null)
        {
            return;
        }

        // Find the restored Chapter entities for the missing parts by filename within the same manga from provided list
        HashSet<string> missingFileNames = missingParts
            .Select(p => p.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Chapter> chaptersToUpscale = restoredChapters
            .Where(c =>
                c.MangaId == originalChapter.MangaId && missingFileNames.Contains(c.FileName)
            )
            .ToList();

        // Log if some parts couldn't be located (should be rare)
        HashSet<string> foundSet = chaptersToUpscale
            .Select(c => c.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> notFound = missingFileNames.Except(foundSet).ToList();
        if (notFound.Count != 0)
        {
            logger.LogWarning(
                "Could not find {Count} restored chapters for missing parts: {Parts}. They will be skipped.",
                notFound.Count,
                string.Join(", ", notFound)
            );
        }

        foreach (Chapter partChapter in chaptersToUpscale)
        {
            // Queue an individual upscale task for each missing part using the original chapter's profile
            var task = new UpscaleTask(partChapter, originalChapter.UpscalerProfile!);
            await taskQueue.EnqueueAsync(task);
            logger.LogInformation(
                "Queued individual upscale task for missing part {FileName} (ChapterId {ChapterId})",
                partChapter.FileName,
                partChapter.Id
            );
        }
    }

    private async Task CleanupExistingChaptersAsync(
        List<FoundChapter> restoredChapters,
        int mangaId,
        bool shouldCleanupUpscaledToo,
        CancellationToken cancellationToken
    )
    {
        // Get the filenames of the chapters we're about to restore
        HashSet<string> fileNamesToRestore = restoredChapters.Select(rc => rc.FileName).ToHashSet();

        // Find any existing chapters with the same filenames for this manga
        List<Chapter> existingChapters = await dbContext
            .Chapters.Where(c => c.MangaId == mangaId && fileNamesToRestore.Contains(c.FileName))
            .ToListAsync(cancellationToken);

        if (existingChapters.Any())
        {
            logger.LogInformation(
                "Removing {Count} existing chapters to avoid constraint violations: {FileNames}",
                existingChapters.Count,
                string.Join(", ", existingChapters.Select(c => c.FileName))
            );

            // Remove existing chapters
            dbContext.Chapters.RemoveRange(existingChapters);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CancelPendingRepairTaskForChapterAsync(
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Load pending tasks and filter in-memory for the specific RepairUpscaleTask bound to this chapter
            var pending = await dbContext
                .PersistedTasks.Where(t => t.Status == PersistedTaskStatus.Pending)
                .ToListAsync(cancellationToken);

            foreach (PersistedTask task in pending)
            {
                if (task.Data is RepairUpscaleTask repair && repair.ChapterId == chapterId)
                {
                    logger.LogInformation(
                        "Removing pending RepairUpscaleTask for merged chapter {ChapterId}",
                        chapterId
                    );
                    await taskQueue.RemoveTaskAsync(task);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: if we fail to remove a pending task, the processor will later skip it gracefully
            logger.LogWarning(
                ex,
                "Failed to remove pending repair task for chapter {ChapterId}",
                chapterId
            );
        }
    }
}
