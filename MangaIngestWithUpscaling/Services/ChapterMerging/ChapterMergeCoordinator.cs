using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeCoordinator(
    ApplicationDbContext dbContext,
    IChapterPartMerger chapterPartMerger,
    IChapterMergeUpscaleTaskManager upscaleTaskManager,
    ILogger<ChapterMergeCoordinator> logger) : IChapterMergeCoordinator
{
    public async Task ProcessExistingChapterPartsForMergingAsync(
        Manga manga,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Load the library if not already loaded
            if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
            {
                await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
            }

            Library library = manga.Library;

            // Only proceed if merging is enabled for this manga/library
            bool shouldMerge = manga.MergeChapterParts ?? library.MergeChapterParts;
            if (!shouldMerge)
            {
                return;
            }

            // Extract chapter numbers from all existing chapters in this manga
            HashSet<string> allChapterNumbers = manga.Chapters
                .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet();

            // Get IDs of chapters that have already been merged to avoid re-processing them
            List<int> seriesChapterIds = manga.Chapters.Select(c => c.Id).ToList();
            HashSet<int> mergedChapterIds = await dbContext.MergedChapterInfos
                .Where(m => seriesChapterIds.Contains(m.ChapterId))
                .Select(m => m.ChapterId)
                .ToHashSetAsync(cancellationToken);

            // Process existing chapters to identify and merge eligible chapter parts
            ChapterMergeResult mergeResult = await chapterPartMerger.ProcessExistingChapterPartsAsync(
                manga.Chapters.ToList(),
                library.NotUpscaledLibraryPath,
                manga.PrimaryTitle!,
                allChapterNumbers,
                mergedChapterIds,
                cancellationToken);

            if (!mergeResult.MergeInformation.Any())
            {
                return;
            }

            logger.LogInformation(
                "Found {GroupCount} groups of existing chapter parts that can be merged for series {SeriesTitle}",
                mergeResult.MergeInformation.Count, manga.PrimaryTitle);

            // Calculate series library path for upscaled chapter handling
            string seriesLibraryPath = Path.Combine(
                library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(manga.PrimaryTitle!));

            // Process each group of mergeable chapter parts
            foreach (MergeInfo mergeInfo in mergeResult.MergeInformation)
            {
                // Find the database chapters to update based on the original parts filenames
                List<Chapter> dbChaptersToUpdate = manga.Chapters
                    .Where(c => mergeInfo.OriginalParts.Any(p => p.FileName == c.FileName))
                    .ToList();

                if (dbChaptersToUpdate.Count == mergeInfo.OriginalParts.Count)
                {
                    // Check if merging is compatible with current upscale task status
                    UpscaleCompatibilityResult compatibility =
                        await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                            dbChaptersToUpdate, cancellationToken);

                    if (!compatibility.CanMerge)
                    {
                        logger.LogInformation(
                            "Skipping merge of chapter parts for {MergedFileName}: {Reason}",
                            mergeInfo.MergedChapter.FileName, compatibility.Reason);
                        continue;
                    }

                    // Handle merging of upscaled versions if they exist
                    await HandleUpscaledChapterMergingAsync(
                        dbChaptersToUpdate, mergeInfo, library, seriesLibraryPath, cancellationToken);

                    // Update database records to reflect the merge
                    await UpdateDatabaseForMergeAsync(mergeInfo, dbChaptersToUpdate, cancellationToken);

                    // Handle upscale task management
                    await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                        dbChaptersToUpdate, mergeInfo, library, cancellationToken);

                    logger.LogInformation(
                        "Successfully merged {PartCount} existing chapter parts into {MergedFileName} for series {SeriesTitle}",
                        mergeInfo.OriginalParts.Count, mergeInfo.MergedChapter.FileName, manga.PrimaryTitle);
                }
                else
                {
                    logger.LogWarning(
                        "Mismatch between expected chapters ({Expected}) and found chapters ({Found}) for merge {MergedFileName}",
                        mergeInfo.OriginalParts.Count, dbChaptersToUpdate.Count, mergeInfo.MergedChapter.FileName);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during chapter part merging for series {SeriesTitle}",
                manga.PrimaryTitle);
        }
    }

    public async Task UpdateDatabaseForMergeAsync(
        MergeInfo mergeInfo,
        List<Chapter> originalChapters,
        CancellationToken cancellationToken = default)
    {
        // Keep the first chapter record and update it to represent the merged chapter
        Chapter primaryChapter = originalChapters.First();
        primaryChapter.FileName = mergeInfo.MergedChapter.FileName;

        // Load library reference if not already loaded
        if (!dbContext.Entry(primaryChapter).Reference(c => c.Manga).IsLoaded)
        {
            await dbContext.Entry(primaryChapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(primaryChapter.Manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(primaryChapter.Manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        }

        Library library = primaryChapter.Manga.Library;
        string seriesLibraryPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(primaryChapter.Manga.PrimaryTitle!));

        // Calculate the correct relative path from the library root
        string mergedFilePath = Path.Combine(seriesLibraryPath, mergeInfo.MergedChapter.FileName);
        primaryChapter.RelativePath = Path.GetRelativePath(library.NotUpscaledLibraryPath, mergedFilePath);

        // Create merge tracking record
        var mergedChapterInfo = new MergedChapterInfo
        {
            ChapterId = primaryChapter.Id,
            OriginalParts = mergeInfo.OriginalParts,
            MergedChapterNumber = mergeInfo.BaseChapterNumber,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.MergedChapterInfos.Add(mergedChapterInfo);

        // Remove the other chapter records
        List<Chapter> chaptersToRemove = originalChapters.Skip(1).ToList();
        dbContext.Chapters.RemoveRange(chaptersToRemove);
    }

    public async Task MergeChaptersAsync(
        List<Chapter> chapters,
        Library library,
        CancellationToken cancellationToken = default)
    {
        // Load necessary references
        foreach (Chapter chapter in chapters)
        {
            if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
            {
                await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
            }
        }

        Manga manga = chapters.First().Manga;
        string seriesLibraryPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(manga.PrimaryTitle!));

        // Get all chapter numbers for processing
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Convert chapters to FoundChapter format for merging
        List<FoundChapter> foundChapters = chapters.Select(c => new FoundChapter(
            c.FileName,
            c.RelativePath,
            ChapterStorageType.Cbz,
            new ExtractedMetadata(manga.PrimaryTitle!, null,
                ChapterNumberHelper.ExtractChapterNumber(c.FileName) ?? "0")
        )).ToList();

        // Perform the merge using ChapterPartMerger
        ChapterMergeResult mergeResult = await chapterPartMerger.ProcessExistingChapterPartsAsync(
            chapters,
            library.NotUpscaledLibraryPath,
            manga.PrimaryTitle!,
            allChapterNumbers,
            new HashSet<int>(), // No excluded chapters for explicit merges
            cancellationToken);

        if (mergeResult.MergeInformation.Any())
        {
            MergeInfo mergeInfo = mergeResult.MergeInformation.First();

            // Check upscale compatibility
            UpscaleCompatibilityResult compatibility = await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                chapters, cancellationToken);

            if (!compatibility.CanMerge)
            {
                throw new InvalidOperationException($"Cannot merge chapters: {compatibility.Reason}");
            }

            // Handle merging of upscaled versions if they exist
            await HandleUpscaledChapterMergingAsync(
                chapters, mergeInfo, library, seriesLibraryPath, cancellationToken);

            // Update database records to reflect the merge
            await UpdateDatabaseForMergeAsync(mergeInfo, chapters, cancellationToken);

            // Handle upscale task management
            await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                chapters, mergeInfo, library, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Successfully merged {PartCount} chapter parts into {MergedFileName}",
                chapters.Count, mergeInfo.MergedChapter.FileName);
        }
    }

    private async Task HandleUpscaledChapterMergingAsync(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        string seriesLibraryPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath))
        {
            return; // No upscaled library is configured for this library
        }

        // Determine the path where upscaled versions of the chapters should be located
        string upscaledSeriesPath = Path.Combine(
            library.UpscaledLibraryPath,
            PathEscaper.EscapeFileName(mergeInfo.MergedChapter.Metadata.Series ?? "Unknown"));

        var upscaledParts = new List<FoundChapter>();
        bool allPartsHaveUpscaledVersions = true;

        // Check if upscaled versions exist for all the original chapter parts
        foreach (OriginalChapterPart originalPart in mergeInfo.OriginalParts)
        {
            string upscaledFilePath = Path.Combine(upscaledSeriesPath, originalPart.FileName);

            if (File.Exists(upscaledFilePath))
            {
                upscaledParts.Add(new FoundChapter(
                    originalPart.FileName,
                    Path.GetRelativePath(library.UpscaledLibraryPath, upscaledFilePath),
                    ChapterStorageType.Cbz,
                    originalPart.Metadata));
            }
            else
            {
                allPartsHaveUpscaledVersions = false;
                break;
            }
        }

        if (!allPartsHaveUpscaledVersions)
        {
            logger.LogDebug(
                "Upscaled versions not found for all chapter parts with base number {BaseNumber}, skipping upscaled merge",
                mergeInfo.BaseChapterNumber);
            return;
        }

        // Merge the upscaled chapter parts using the core merging functionality
        try
        {
            var (upscaledMergedChapter, upscaledOriginalParts) = await chapterPartMerger.MergeChapterPartsAsync(
                upscaledParts,
                library.UpscaledLibraryPath, // Base library path for resolving relative paths
                upscaledSeriesPath, // Target directory for the merged chapter file
                mergeInfo.BaseChapterNumber,
                mergeInfo.MergedChapter.Metadata,
                null,
                cancellationToken);

            // Clean up original upscaled chapter part files after successful merging
            foreach (OriginalChapterPart originalPart in mergeInfo.OriginalParts)
            {
                try
                {
                    string upscaledPartFilePath = Path.Combine(upscaledSeriesPath, originalPart.FileName);
                    if (File.Exists(upscaledPartFilePath))
                    {
                        File.Delete(upscaledPartFilePath);
                        logger.LogInformation("Deleted original upscaled chapter part file: {FilePath}",
                            upscaledPartFilePath);
                    }
                    else
                    {
                        logger.LogWarning("Original upscaled chapter part file not found for deletion: {FilePath}",
                            upscaledPartFilePath);
                    }
                }
                catch (Exception deleteEx)
                {
                    logger.LogError(deleteEx, "Failed to delete original upscaled chapter part file: {PartFileName}",
                        originalPart.FileName);
                }
            }

            logger.LogInformation(
                "Successfully merged {PartCount} upscaled chapter parts into {MergedFileName}",
                upscaledParts.Count, upscaledMergedChapter.FileName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to merge upscaled chapter parts for base number {BaseNumber}",
                mergeInfo.BaseChapterNumber);
        }
    }
}