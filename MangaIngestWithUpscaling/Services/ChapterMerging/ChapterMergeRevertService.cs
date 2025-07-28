using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeRevertService(
    ApplicationDbContext dbContext,
    IChapterPartMerger chapterPartMerger,
    IChapterChangedNotifier chapterChangedNotifier,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    ILogger<ChapterMergeRevertService> logger) : IChapterMergeRevertService
{
    public async Task<List<Chapter>> RevertMergedChapterAsync(Chapter chapter,
        CancellationToken cancellationToken = default)
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        if (mergeInfo == null)
        {
            throw new InvalidOperationException(
                $"Chapter {chapter.FileName} is not a merged chapter and cannot be reverted.");
        }

        // Load chapter dependencies
        if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
        {
            await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(chapter.Manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(chapter.Manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        }

        Library library = chapter.Manga.Library;
        string mergedChapterPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);

        if (!File.Exists(mergedChapterPath))
        {
            throw new FileNotFoundException($"Merged chapter file not found: {mergedChapterPath}");
        }

        try
        {
            // Get original parts information from strongly typed property
            List<OriginalChapterPart>? originalParts = mergeInfo.OriginalParts;
            if (originalParts == null || !originalParts.Any())
            {
                throw new InvalidOperationException("Invalid merge information: no original parts found.");
            }

            logger.LogInformation("Reverting merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName, originalParts.Count);

            // Create output directory for the restored parts
            string seriesDirectory = Path.Combine(library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(chapter.Manga.PrimaryTitle!));

            // Restore the original parts
            List<FoundChapter> restoredChapters = await chapterPartMerger.RestoreChapterPartsAsync(
                mergedChapterPath, originalParts, seriesDirectory, cancellationToken);

            // Clean up any existing chapters with the same filenames to avoid constraint violations
            await CleanupExistingChaptersAsync(restoredChapters, chapter.MangaId, chapter.IsUpscaled,
                cancellationToken);

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
                    RelativePath = Path.GetRelativePath(library.NotUpscaledLibraryPath,
                        Path.Combine(seriesDirectory, restoredChapter.FileName)),
                    IsUpscaled = chapter.IsUpscaled,
                    UpscalerProfileId = chapter.UpscalerProfileId,
                    UpscalerProfile = chapter.UpscalerProfile
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
                await CreateUpscaledRestoredChaptersAsync(chapter, library, mergeInfo, cancellationToken);
            }

            // Remove the merged chapter info after upscaled chapters are processed
            dbContext.MergedChapterInfos.Remove(mergeInfo);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Successfully reverted merged chapter {ChapterFile} to {PartCount} original parts",
                chapter.FileName, originalParts.Count);

            return restoredChapterEntities;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revert merged chapter {ChapterFile}", chapter.FileName);
            throw;
        }
    }

    public async Task<bool> CanRevertChapterAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        MergedChapterInfo? mergeInfo = await GetMergeInfoAsync(chapter, cancellationToken);
        return mergeInfo != null;
    }

    public async Task<MergedChapterInfo?> GetMergeInfoAsync(Chapter chapter,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.MergedChapterInfos
            .FirstOrDefaultAsync(m => m.ChapterId == chapter.Id, cancellationToken);
    }

    private async Task CreateUpscaledRestoredChaptersAsync(Chapter originalChapter,
        Library library, MergedChapterInfo? mergeInfo, CancellationToken cancellationToken)
    {
        // Get the path to the upscaled merged chapter file
        string upscaledMergedChapterPath = Path.Combine(library.UpscaledLibraryPath!, originalChapter.RelativePath);

        if (!File.Exists(upscaledMergedChapterPath))
        {
            logger.LogWarning("Upscaled merged chapter file not found: {UpscaledPath}, skipping upscaled restoration",
                upscaledMergedChapterPath);
            return;
        }

        // Create upscaled directory structure for restored parts
        string upscaledSeriesDirectory = Path.Combine(library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(originalChapter.Manga.PrimaryTitle!));
        Directory.CreateDirectory(upscaledSeriesDirectory);

        // Get the merge info to know the original parts structure
        if (mergeInfo?.OriginalParts == null)
        {
            logger.LogWarning("No merge info found for upscaled chapter restoration, skipping");
            return;
        }

        // Restore the upscaled chapter parts using the same logic as regular restoration
        List<FoundChapter> upscaledRestoredChapters = await chapterPartMerger.RestoreChapterPartsAsync(
            upscaledMergedChapterPath, mergeInfo.OriginalParts, upscaledSeriesDirectory, cancellationToken);

        // Add upscaler.json to each restored upscaled part
        foreach (FoundChapter upscaledRestoredChapter in upscaledRestoredChapters)
        {
            string upscaledChapterPath = Path.Combine(upscaledSeriesDirectory, upscaledRestoredChapter.FileName);

            // Add upscaler.json to the upscaled chapter
            using ZipArchive archive = ZipFile.Open(upscaledChapterPath, ZipArchiveMode.Update);
            await upscalerJsonHandlingService.WriteUpscalerJsonAsync(archive, originalChapter.UpscalerProfile!,
                cancellationToken);
        }

        // Delete the upscaled merged chapter file
        File.Delete(upscaledMergedChapterPath);

        logger.LogInformation("Restored {Count} upscaled chapter parts from {MergedFile}",
            upscaledRestoredChapters.Count, Path.GetFileName(upscaledMergedChapterPath));
    }

    private async Task CleanupExistingChaptersAsync(List<FoundChapter> restoredChapters, int mangaId,
        bool shouldCleanupUpscaledToo, CancellationToken cancellationToken)
    {
        // Get the filenames of the chapters we're about to restore
        HashSet<string> fileNamesToRestore = restoredChapters.Select(rc => rc.FileName).ToHashSet();

        // Find any existing chapters with the same filenames for this manga
        List<Chapter> existingChapters = await dbContext.Chapters
            .Where(c => c.MangaId == mangaId && fileNamesToRestore.Contains(c.FileName))
            .ToListAsync(cancellationToken);

        if (existingChapters.Any())
        {
            logger.LogInformation("Removing {Count} existing chapters to avoid constraint violations: {FileNames}",
                existingChapters.Count, string.Join(", ", existingChapters.Select(c => c.FileName)));

            // Remove existing chapters
            dbContext.Chapters.RemoveRange(existingChapters);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}