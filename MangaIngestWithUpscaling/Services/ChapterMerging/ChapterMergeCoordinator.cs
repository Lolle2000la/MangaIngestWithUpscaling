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
    IMetadataHandlingService metadataHandlingService,
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

    public async Task<List<MergeInfo>> MergeSelectedChaptersAsync(List<Chapter> selectedChapters, bool includeLatestChapters = false, CancellationToken cancellationToken = default)
    {
        if (!selectedChapters.Any())
        {
            return new List<MergeInfo>();
        }

        // Load necessary references
        foreach (Chapter chapter in selectedChapters)
        {
            if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
            {
                await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
            }
        }

        Manga manga = selectedChapters.First().Manga;

        // Load library reference
        if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        }

        Library library = manga.Library;

        // Get all existing chapter numbers for latest chapter detection
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Convert selected chapters to FoundChapter format for processing
        List<FoundChapter> foundChapters = selectedChapters.Select(c => new FoundChapter(
            c.FileName,
            Path.GetFileName(c.RelativePath), // Use just filename, consistent with existing system
            ChapterStorageType.Cbz,
            GetChapterMetadata(c)
        )).ToList();

        // Group chapters for merging, respecting latest chapter inclusion setting
        Dictionary<string, List<FoundChapter>> mergeGroups = chapterPartMerger.GroupChapterPartsForMerging(
            foundChapters,
            baseNumber => !includeLatestChapters && IsLatestChapter(baseNumber, allChapterNumbers));

        if (!mergeGroups.Any())
        {
            return new List<MergeInfo>();
        }

        var completedMerges = new List<MergeInfo>();
        string seriesLibraryPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(manga.PrimaryTitle!));

        // Process each merge group
        foreach (var (baseNumber, chapterParts) in mergeGroups)
        {
            try
            {
                // Get the original chapters for this group
                List<Chapter> originalChapters = selectedChapters
                    .Where(c => chapterParts.Any(fc => fc.FileName == c.FileName))
                    .ToList();

                // Get the original chapter title from the first part to preserve format
                FoundChapter firstPart = chapterParts.First();
                string? originalChapterTitle = firstPart.Metadata?.ChapterTitle;
                string? mergedChapterTitle = GenerateMergedChapterTitle(originalChapterTitle, baseNumber);

                // Create target metadata for merged chapter
                var targetMetadata = new ExtractedMetadata(
                    manga.PrimaryTitle!,
                    mergedChapterTitle,
                    baseNumber);

                // Perform the merge
                var (mergedChapter, originalParts) = await chapterPartMerger.MergeChapterPartsAsync(
                    chapterParts,
                    seriesLibraryPath,
                    seriesLibraryPath,
                    baseNumber,
                    targetMetadata,
                    null,
                    cancellationToken);

                // Create MergeInfo with corrected relative path
                var correctedMergedChapter = new FoundChapter(
                    mergedChapter.FileName,
                    Path.Combine(PathEscaper.EscapeFileName(manga.PrimaryTitle!), mergedChapter.FileName),
                    mergedChapter.StorageType,
                    mergedChapter.Metadata);

                var mergeInfo = new MergeInfo(correctedMergedChapter, originalParts, baseNumber);

                // Check upscale compatibility
                UpscaleCompatibilityResult compatibility = await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                    originalChapters, cancellationToken);

                if (!compatibility.CanMerge)
                {
                    logger.LogWarning("Upscale compatibility check failed for merge group {BaseNumber}: {Reason}",
                        baseNumber, compatibility.Reason);
                    continue;
                }

                // Update database for the merge
                await UpdateDatabaseForMergeAsync(mergeInfo, originalChapters, cancellationToken);

                // Handle upscale task management
                await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                    originalChapters, mergeInfo, library, cancellationToken);

                // Handle merging of upscaled versions if they exist
                await HandleUpscaledChapterMergingAsync(
                    originalChapters, mergeInfo, library, seriesLibraryPath, cancellationToken);

                completedMerges.Add(mergeInfo);

                logger.LogInformation("Successfully completed manual merge for base number {BaseNumber} with {PartCount} parts",
                    baseNumber, chapterParts.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to merge chapter parts for base number {BaseNumber}", baseNumber);
                // Continue with other merge groups
            }
        }

        // Save all database changes
        await dbContext.SaveChangesAsync(cancellationToken);

        return completedMerges;
    }

    public async Task<Dictionary<string, List<Chapter>>> GetValidMergeGroupsAsync(List<Chapter> selectedChapters, bool includeLatestChapters = false, CancellationToken cancellationToken = default)
    {
        if (!selectedChapters.Any())
        {
            return new Dictionary<string, List<Chapter>>();
        }

        // Load necessary references
        foreach (Chapter chapter in selectedChapters)
        {
            if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
            {
                await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
            }
        }

        Manga manga = selectedChapters.First().Manga;

        // Get all existing chapter numbers for latest chapter detection
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Convert selected chapters to FoundChapter format for validation
        List<FoundChapter> foundChapters = selectedChapters.Select(c => new FoundChapter(
            c.FileName,
            Path.GetFileName(c.RelativePath),
            ChapterStorageType.Cbz,
            GetChapterMetadata(c)
        )).ToList();

        // Group chapters for merging
        Dictionary<string, List<FoundChapter>> foundChapterGroups = chapterPartMerger.GroupChapterPartsForMerging(
            foundChapters,
            baseNumber => !includeLatestChapters && IsLatestChapter(baseNumber, allChapterNumbers));

        // Convert back to Chapter groups
        var result = new Dictionary<string, List<Chapter>>();
        foreach (var (baseNumber, foundChapterList) in foundChapterGroups)
        {
            var chapterList = foundChapterList
                .Select(fc => selectedChapters.First(c => c.FileName == fc.FileName))
                .ToList();
            result[baseNumber] = chapterList;
        }

        return result;
    }

    private bool IsLatestChapter(string baseNumber, HashSet<string> allChapterNumbers)
    {
        if (!decimal.TryParse(baseNumber, out decimal baseNum))
            return false;

        foreach (string chapterNumber in allChapterNumbers)
        {
            if (decimal.TryParse(chapterNumber, out decimal num))
            {
                decimal chapterBaseNum = Math.Floor(num);
                if (chapterBaseNum > baseNum)
                    return false;
            }
        }

        return true;
    }

    private string? GenerateMergedChapterTitle(string? originalTitle, string baseChapterNumber)
    {
        if (string.IsNullOrEmpty(originalTitle))
        {
            return null;
        }

        // Replace the chapter number in the title with the base number
        return ChapterNumberHelper.ChapterNumberRegex().Replace(originalTitle,
            match => match.Value.Replace(match.Groups["num"].Value +
                                         (string.IsNullOrEmpty(match.Groups["subnum"].Value)
                                             ? ""
                                             : "." + match.Groups["subnum"].Value),
                baseChapterNumber));
    }

    private ExtractedMetadata GetChapterMetadata(Chapter chapter)
    {
        try
        {
            // Construct the full path to the chapter file
            string fullPath = chapter.NotUpscaledFullPath;

            if (!File.Exists(fullPath))
            {
                // Fallback to filename-based metadata if file doesn't exist
                return new ExtractedMetadata(
                    chapter.Manga.PrimaryTitle!,
                    Path.GetFileNameWithoutExtension(chapter.FileName),
                    ChapterNumberHelper.ExtractChapterNumber(chapter.FileName) ?? "0");
            }

            // Use MetadataHandlingService to extract the actual metadata from ComicInfo.xml
            ExtractedMetadata fileMetadata = metadataHandlingService.GetSeriesAndTitleFromComicInfo(fullPath);

            // Use the file's metadata but override the series with the manga's primary title
            return fileMetadata with { Series = chapter.Manga.PrimaryTitle! };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract metadata from {FileName}, using fallback",
                chapter.FileName);
            return new ExtractedMetadata(
                chapter.Manga.PrimaryTitle!,
                Path.GetFileNameWithoutExtension(chapter.FileName),
                ChapterNumberHelper.ExtractChapterNumber(chapter.FileName) ?? "0");
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