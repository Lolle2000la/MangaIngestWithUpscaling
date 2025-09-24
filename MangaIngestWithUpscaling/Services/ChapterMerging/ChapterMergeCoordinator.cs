using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Services.ChapterMerging;

[RegisterScoped]
public class ChapterMergeCoordinator(
    ApplicationDbContext dbContext,
    IChapterPartMerger chapterPartMerger,
    IChapterMergeUpscaleTaskManager upscaleTaskManager,
    ITaskQueue taskQueue,
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

            // Get base chapter numbers that already have merged chapters to prevent conflicts
            HashSet<string> existingMergedBaseNumbers = await dbContext.MergedChapterInfos
                .Where(m => seriesChapterIds.Contains(m.ChapterId))
                .Select(m => m.MergedChapterNumber)
                .ToHashSetAsync(cancellationToken);

            // Process existing chapters to identify and merge eligible chapter parts
            ChapterMergeResult mergeResult = await chapterPartMerger.ProcessExistingChapterPartsAsync(
                manga.Chapters.ToList(),
                library.NotUpscaledLibraryPath,
                manga.PrimaryTitle!,
                allChapterNumbers,
                mergedChapterIds,
                existingMergedBaseNumbers,
                cancellationToken);

            if (!mergeResult.MergeInformation.Any())
            {
                return;
            }

            // Separate merge groups into new merges and additions to existing merged chapters
            List<MergeInfo> newMergeInfos = mergeResult.MergeInformation
                .Where(mergeInfo => !existingMergedBaseNumbers.Contains(mergeInfo.BaseChapterNumber))
                .ToList();

            List<MergeInfo> existingMergeAdditions = mergeResult.MergeInformation
                .Where(mergeInfo => existingMergedBaseNumbers.Contains(mergeInfo.BaseChapterNumber))
                .ToList();

            if (!newMergeInfos.Any() && !existingMergeAdditions.Any())
            {
                return;
            }

            logger.LogDebug(
                "Found {NewMergeCount} new merge groups and {ExistingAdditionCount} groups to add to existing merged chapters for series {SeriesTitle}",
                newMergeInfos.Count, existingMergeAdditions.Count, manga.PrimaryTitle);

            // Calculate series library path for upscaled chapter handling
            string seriesLibraryPath = Path.Combine(
                library.NotUpscaledLibraryPath,
                PathEscaper.EscapeFileName(manga.PrimaryTitle!));

            // Process new merge groups first
            foreach (MergeInfo mergeInfo in newMergeInfos)
            {
                // Find the database chapters to update based on the original parts filenames
                List<Chapter> dbChaptersToUpdate = manga.Chapters
                    .Where(c => mergeInfo.OriginalParts.Any(p => p.FileName == c.FileName))
                    .ToList();

                if (dbChaptersToUpdate.Count == mergeInfo.OriginalParts.Count)
                {
                    await ProcessSingleMergeGroupAsync(mergeInfo, dbChaptersToUpdate, library, seriesLibraryPath,
                        cancellationToken);

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

            // Process additions to existing merged chapters
            foreach (MergeInfo mergeInfo in existingMergeAdditions)
            {
                await ProcessAdditionToExistingMergedChapterAsync(mergeInfo, manga, library, seriesLibraryPath,
                    cancellationToken);
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
        await LoadChapterReferencesAsync(chapters, cancellationToken);

        Manga manga = chapters.First().Manga;
        string seriesLibraryPath = BuildSeriesLibraryPath(library, manga.PrimaryTitle!);

        // Get all chapter numbers for processing
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Perform the merge using ChapterPartMerger
        ChapterMergeResult mergeResult = await chapterPartMerger.ProcessExistingChapterPartsAsync(
            chapters,
            library.NotUpscaledLibraryPath,
            manga.PrimaryTitle!,
            allChapterNumbers,
            new HashSet<int>(), // No excluded chapters for explicit merges
            new HashSet<string>(), // No existing merged chapters for explicit merges
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
            UpscaledMergeResult upscaledMergeResult = await HandleUpscaledChapterMergingAsync(
                chapters, mergeInfo, library, seriesLibraryPath, cancellationToken);

            // Update database records to reflect the merge
            await UpdateDatabaseForMergeAsync(mergeInfo, chapters, cancellationToken);

            // Handle upscale task management with information about partial merging
            await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                chapters, mergeInfo, library, upscaledMergeResult, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Successfully merged {PartCount} chapter parts into {MergedFileName}",
                chapters.Count, mergeInfo.MergedChapter.FileName);
        }
    }

    public async Task<List<MergeInfo>> MergeSelectedChaptersAsync(List<Chapter> selectedChapters,
        bool includeLatestChapters = false, CancellationToken cancellationToken = default)
    {
        if (!selectedChapters.Any())
        {
            return new List<MergeInfo>();
        }

        // Load necessary references
        await LoadChapterReferencesAsync(selectedChapters, cancellationToken);

        Manga manga = selectedChapters.First().Manga;
        Library library = manga.Library;

        // Get all existing chapter numbers for latest chapter detection
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Convert selected chapters to FoundChapter format for processing
        List<FoundChapter> foundChapters = await ConvertChaptersToFoundChapters(selectedChapters);

        // Group chapters for merging, respecting latest chapter inclusion setting
        Dictionary<string, List<FoundChapter>> mergeGroups = chapterPartMerger.GroupChapterPartsForMerging(
            foundChapters,
            CreateLatestChapterChecker(allChapterNumbers, includeLatestChapters));

        if (!mergeGroups.Any())
        {
            return new List<MergeInfo>();
        }

        var completedMerges = new List<MergeInfo>();
        string seriesLibraryPath = BuildSeriesLibraryPath(library, manga.PrimaryTitle!);

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
                UpscaleCompatibilityResult compatibility =
                    await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                        originalChapters, cancellationToken);

                if (!compatibility.CanMerge)
                {
                    logger.LogWarning("Upscale compatibility check failed for merge group {BaseNumber}: {Reason}",
                        baseNumber, compatibility.Reason);
                    continue;
                }

                // Update database for the merge
                await UpdateDatabaseForMergeAsync(mergeInfo, originalChapters, cancellationToken);

                // Delete original chapter part files after successful merging
                DeleteOriginalChapterPartFiles(mergeInfo, library);

                // Handle merging of upscaled versions if they exist
                UpscaledMergeResult upscaledMergeResult = await HandleUpscaledChapterMergingAsync(
                    originalChapters, mergeInfo, library, seriesLibraryPath, cancellationToken);

                // Handle upscale task management with information about partial merging
                await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                    originalChapters, mergeInfo, library, upscaledMergeResult, cancellationToken);

                completedMerges.Add(mergeInfo);

                logger.LogInformation(
                    "Successfully completed manual merge for base number {BaseNumber} with {PartCount} parts",
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

    public async Task<Dictionary<string, List<Chapter>>> GetValidMergeGroupsAsync(List<Chapter> selectedChapters,
        bool includeLatestChapters = false, CancellationToken cancellationToken = default)
    {
        if (!selectedChapters.Any())
        {
            return new Dictionary<string, List<Chapter>>();
        }

        // Load necessary references
        await LoadChapterReferencesAsync(selectedChapters, cancellationToken);

        Manga manga = selectedChapters.First().Manga;

        // Get all existing chapter numbers for latest chapter detection
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        // Convert selected chapters to FoundChapter format for validation
        List<FoundChapter> foundChapters = await ConvertChaptersToFoundChapters(selectedChapters);

        // Group chapters for merging
        Dictionary<string, List<FoundChapter>> foundChapterGroups = chapterPartMerger.GroupChapterPartsForMerging(
            foundChapters,
            CreateLatestChapterChecker(allChapterNumbers, includeLatestChapters));

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

    public async Task<bool> CanChapterBeAddedToExistingMergedAsync(Chapter chapter,
        CancellationToken cancellationToken = default)
    {
        // Load necessary references
        await LoadChapterReferencesAsync([chapter], cancellationToken);

        Manga manga = chapter.Manga;

        // Extract chapter number
        string? chapterNumber = ChapterNumberHelper.ExtractChapterNumber(chapter.FileName);
        if (chapterNumber == null)
        {
            return false;
        }

        // Get the base chapter number
        if (!decimal.TryParse(chapterNumber, out decimal fullNumber))
        {
            return false;
        }

        string baseNumber = Math.Floor(fullNumber).ToString();

        // Check if there's an existing merged chapter with this base number
        List<int> seriesChapterIds = manga.Chapters.Select(c => c.Id).ToList();
        bool hasExistingMergedChapter = await dbContext.MergedChapterInfos
            .Where(m => seriesChapterIds.Contains(m.ChapterId) && m.MergedChapterNumber == baseNumber)
            .AnyAsync(cancellationToken);

        if (!hasExistingMergedChapter)
        {
            return false;
        }

        // Check if this is a proper chapter part (has decimal part)
        decimal decimalPart = fullNumber - Math.Floor(fullNumber);
        return decimalPart > 0 && decimalPart < 1;
    }

    public async Task<MergeActionInfo> GetPossibleMergeActionsAsync(List<Chapter> chapters,
        bool includeLatestChapters = false, CancellationToken cancellationToken = default)
    {
        var result = new MergeActionInfo();

        if (!chapters.Any())
        {
            logger.LogDebug("GetPossibleMergeActionsAsync: No chapters provided, returning empty result");
            return result;
        }

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: Analyzing {ChapterCount} chapters, includeLatestChapters={IncludeLatest}",
            chapters.Count, includeLatestChapters);

        // Load necessary references
        await LoadChapterReferencesAsync(chapters, cancellationToken);

        Manga manga = chapters.First().Manga;

        // Get all existing chapter numbers for latest chapter detection
        HashSet<string> allChapterNumbers = manga.Chapters
            .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet();

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: Found {AllChapterCount} total chapters in manga: [{ChapterNumbers}]",
            allChapterNumbers.Count, string.Join(", ", allChapterNumbers));

        // Get existing merged base numbers
        List<int> seriesChapterIds = manga.Chapters.Select(c => c.Id).ToList();
        HashSet<string> existingMergedBaseNumbers = await dbContext.MergedChapterInfos
            .Where(m => seriesChapterIds.Contains(m.ChapterId))
            .Select(m => m.MergedChapterNumber)
            .ToHashSetAsync(cancellationToken);

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: Found {MergedCount} existing merged base numbers: [{MergedNumbers}]",
            existingMergedBaseNumbers.Count, string.Join(", ", existingMergedBaseNumbers));

        // Convert chapters to FoundChapter format for processing
        List<FoundChapter> foundChapters = await ConvertChaptersToFoundChapters(chapters);

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: Converted to {FoundChapterCount} FoundChapters: [{FoundChapterNames}]",
            foundChapters.Count, string.Join(", ", foundChapters.Select(fc => fc.FileName)));

        var latestChapterChecker = CreateLatestChapterChecker(allChapterNumbers, includeLatestChapters);

        // Get new merge groups (chapters that can form new merged chapters)
        Dictionary<string, List<FoundChapter>> newMergeGroups = chapterPartMerger.GroupChapterPartsForMerging(
            foundChapters,
            latestChapterChecker);

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: GroupChapterPartsForMerging returned {NewMergeGroupCount} groups: [{NewMergeGroups}]",
            newMergeGroups.Count, string.Join(", ", newMergeGroups.Keys));

        // Filter out groups that would conflict with existing merged chapters
        Dictionary<string, List<FoundChapter>> validNewMergeGroups = newMergeGroups
            .Where(kvp => !existingMergedBaseNumbers.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: After filtering conflicts, {ValidNewMergeGroupCount} valid new merge groups: [{ValidNewMergeGroups}]",
            validNewMergeGroups.Count, string.Join(", ", validNewMergeGroups.Keys));

        // Convert back to Chapter groups for new merges
        foreach ((string baseNumber, List<FoundChapter> foundChapterList) in validNewMergeGroups)
        {
            List<Chapter> chapterList = foundChapterList
                .Select(fc => chapters.First(c => c.FileName == fc.FileName))
                .ToList();
            result.NewMergeGroups[baseNumber] = chapterList;
        }

        // Get chapters that can be added to existing merged chapters
        Dictionary<string, List<FoundChapter>> additionsToExisting =
            chapterPartMerger.GroupChaptersForAdditionToExistingMerged(
                foundChapters,
                existingMergedBaseNumbers,
                latestChapterChecker);

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: GroupChaptersForAdditionToExistingMerged returned {AdditionCount} groups: [{AdditionGroups}]",
            additionsToExisting.Count, string.Join(", ", additionsToExisting.Keys));

        // Convert back to Chapter groups for additions
        foreach ((string baseNumber, List<FoundChapter> foundChapterList) in additionsToExisting)
        {
            List<Chapter> chapterList = foundChapterList
                .Select(fc => chapters.First(c => c.FileName == fc.FileName))
                .ToList();
            result.AdditionsToExistingMerged[baseNumber] = chapterList;
        }

        logger.LogDebug(
            "GetPossibleMergeActionsAsync: Final result - NewMergeGroups: {NewCount}, AdditionsToExistingMerged: {AdditionCount}, HasAnyMergePossibilities: {HasAny}",
            result.NewMergeGroups.Count, result.AdditionsToExistingMerged.Count, result.HasAnyMergePossibilities);

        return result;
    }

    public async Task<bool> IsChapterPartAlreadyMergedAsync(string chapterFileName, Manga manga,
        CancellationToken cancellationToken = default)
    {
        // Extract chapter number from filename
        string? chapterNumber = ChapterNumberHelper.ExtractChapterNumber(chapterFileName);
        if (chapterNumber == null)
        {
            return false;
        }

        // Get all existing merged chapters for this manga
        List<int> seriesChapterIds = manga.Chapters.Select(c => c.Id).ToList();
        List<MergedChapterInfo> mergedChapterInfos = await dbContext.MergedChapterInfos
            .Where(m => seriesChapterIds.Contains(m.ChapterId))
            .ToListAsync(cancellationToken);

        // Check if this chapter filename is already part of any merged chapter
        foreach (MergedChapterInfo mergedInfo in mergedChapterInfos)
        {
            if (mergedInfo.OriginalParts.Any(part => part.FileName == chapterFileName))
            {
                return true;
            }
        }

        return false;
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

    private async Task<ExtractedMetadata> GetChapterMetadata(Chapter chapter)
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
            ExtractedMetadata fileMetadata =
                await metadataHandlingService.GetSeriesAndTitleFromComicInfoAsync(fullPath);

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

    private async Task<UpscaledMergeResult> HandleUpscaledChapterMergingAsync(
        List<Chapter> originalChapters,
        MergeInfo mergeInfo,
        Library library,
        string seriesLibraryPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath))
        {
            return UpscaledMergeResult.NoUpscaledContent(); // No upscaled library is configured for this library
        }

        // Determine the path where upscaled versions of the chapters should be located
        string upscaledSeriesPath = Path.Combine(
            library.UpscaledLibraryPath,
            PathEscaper.EscapeFileName(mergeInfo.MergedChapter.Metadata.Series ?? "Unknown"));

        var upscaledParts = new List<FoundChapter>();
        var missingUpscaledParts = new List<OriginalChapterPart>();

        // Check which parts have upscaled versions and which are missing
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
                missingUpscaledParts.Add(originalPart);
            }
        }

        // If no upscaled parts exist, let the normal upscale task handle it
        if (upscaledParts.Count == 0)
        {
            logger.LogDebug(
                "No upscaled versions found for chapter parts with base number {BaseNumber}, will rely on full upscale task",
                mergeInfo.BaseChapterNumber);
            return UpscaledMergeResult.NoUpscaledContent();
        }

        // If all parts have upscaled versions, merge them normally
        if (missingUpscaledParts.Count == 0)
        {
            await MergeAllUpscaledPartsAsync(upscaledParts, mergeInfo, library, upscaledSeriesPath, cancellationToken);
            return UpscaledMergeResult.CompleteMerge(upscaledParts.Count);
        }

        // Partial upscaling scenario: merge existing upscaled parts and create repair task for missing ones
        logger.LogInformation(
            "Found {ExistingCount} upscaled parts and {MissingCount} missing parts for base number {BaseNumber}. " +
            "Will merge existing parts and schedule repair task for missing ones.",
            upscaledParts.Count, missingUpscaledParts.Count, mergeInfo.BaseChapterNumber);

        await HandlePartialUpscaledMergingAsync(
            upscaledParts, missingUpscaledParts, mergeInfo, library, upscaledSeriesPath,
            originalChapters, cancellationToken);

        return UpscaledMergeResult.PartialMerge(upscaledParts.Count, missingUpscaledParts.Count);
    }

    private async Task MergeAllUpscaledPartsAsync(
        List<FoundChapter> upscaledParts,
        MergeInfo mergeInfo,
        Library library,
        string upscaledSeriesPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var (upscaledMergedChapter, upscaledOriginalParts) = await chapterPartMerger.MergeChapterPartsAsync(
                upscaledParts,
                library.UpscaledLibraryPath!, // Base library path for resolving relative paths
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
            logger.LogError(ex,
                "Failed to merge upscaled chapter parts for base number {BaseNumber}",
                mergeInfo.BaseChapterNumber);
            throw;
        }
    }

    private async Task HandlePartialUpscaledMergingAsync(
        List<FoundChapter> existingUpscaledParts,
        List<OriginalChapterPart> missingUpscaledParts,
        MergeInfo mergeInfo,
        Library library,
        string upscaledSeriesPath,
        List<Chapter> originalChapters,
        CancellationToken cancellationToken)
    {
        try
        {
            // Create partial merged upscaled chapter from existing parts
            var (partialUpscaledChapter, _) = await chapterPartMerger.MergeChapterPartsAsync(
                existingUpscaledParts,
                library.UpscaledLibraryPath!,
                upscaledSeriesPath,
                mergeInfo.BaseChapterNumber,
                mergeInfo.MergedChapter.Metadata,
                null,
                cancellationToken);

            // Clean up the individual upscaled part files that were merged
            foreach (FoundChapter upscaledPart in existingUpscaledParts)
            {
                try
                {
                    string upscaledPartFilePath = Path.Combine(upscaledSeriesPath, upscaledPart.FileName);
                    if (File.Exists(upscaledPartFilePath))
                    {
                        File.Delete(upscaledPartFilePath);
                        logger.LogDebug("Deleted merged upscaled chapter part file: {FilePath}", upscaledPartFilePath);
                    }
                }
                catch (Exception deleteEx)
                {
                    logger.LogError(deleteEx, "Failed to delete upscaled chapter part file: {PartFileName}",
                        upscaledPart.FileName);
                }
            }

            logger.LogInformation(
                "Successfully created partial merged upscaled chapter {MergedFileName} from {ExistingCount} existing parts. " +
                "Missing {MissingCount} parts will be handled by repair task.",
                partialUpscaledChapter.FileName, existingUpscaledParts.Count, missingUpscaledParts.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to create partial merged upscaled chapter for base number {BaseNumber}. " +
                "Will fall back to full upscale task.",
                mergeInfo.BaseChapterNumber);
            // Don't rethrow - let the normal upscale process handle it
        }
    }

    private void DeleteOriginalChapterPartFiles(
        MergeInfo mergeInfo,
        Library library)
    {
        string seriesLibraryPath = Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(mergeInfo.MergedChapter.Metadata.Series ?? "Unknown"));

        // Delete original chapter part files from filesystem
        foreach (OriginalChapterPart originalPart in mergeInfo.OriginalParts)
        {
            try
            {
                string partFilePath = Path.Combine(seriesLibraryPath, originalPart.FileName);
                if (File.Exists(partFilePath))
                {
                    File.Delete(partFilePath);
                    logger.LogInformation("Deleted original chapter part file: {FilePath}", partFilePath);
                }
                else
                {
                    logger.LogWarning("Original chapter part file not found for deletion: {FilePath}", partFilePath);
                }
            }
            catch (Exception deleteEx)
            {
                logger.LogError(deleteEx, "Failed to delete original chapter part file: {PartFileName}",
                    originalPart.FileName);
            }
        }
    }

    private async Task ProcessAdditionToExistingMergedChapterAsync(
        MergeInfo mergeInfo,
        Manga manga,
        Library library,
        string seriesLibraryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Find the existing merged chapter info for this base number
            List<int> seriesChapterIds = manga.Chapters.Select(c => c.Id).ToList();
            MergedChapterInfo? existingMergedInfo = await dbContext.MergedChapterInfos
                .Where(m => seriesChapterIds.Contains(m.ChapterId) &&
                            m.MergedChapterNumber == mergeInfo.BaseChapterNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingMergedInfo == null)
            {
                logger.LogWarning(
                    "Could not find existing merged chapter info for base number {BaseNumber} in series {SeriesTitle}",
                    mergeInfo.BaseChapterNumber, manga.PrimaryTitle);
                return;
            }

            // Find the existing merged chapter
            Chapter existingMergedChapter = await dbContext.Chapters
                .Where(c => c.Id == existingMergedInfo.ChapterId)
                .FirstAsync(cancellationToken);

            // Find the new chapter parts to add
            List<Chapter> newChapterParts = manga.Chapters
                .Where(c => mergeInfo.OriginalParts.Any(p => p.FileName == c.FileName))
                .ToList();

            if (!newChapterParts.Any())
            {
                logger.LogWarning(
                    "No matching chapters found for addition to existing merged chapter {BaseNumber}",
                    mergeInfo.BaseChapterNumber);
                return;
            }

            // Check upscale compatibility
            UpscaleCompatibilityResult compatibility = await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                newChapterParts, cancellationToken);

            if (!compatibility.CanMerge)
            {
                logger.LogInformation(
                    "Skipping addition to existing merged chapter {BaseNumber}: {Reason}",
                    mergeInfo.BaseChapterNumber, compatibility.Reason);
                return;
            }

            // Add the new parts to the existing merged chapter file
            await AddPartsToExistingMergedChapterAsync(
                existingMergedChapter, mergeInfo.OriginalParts, seriesLibraryPath, cancellationToken);

            // Handle upscaled versions if they exist
            await HandleUpscaledChapterAdditionAsync(
                newChapterParts, mergeInfo, library, seriesLibraryPath, existingMergedChapter, cancellationToken);

            // Update the merged chapter info to include the new parts
            // Create a new list to ensure Entity Framework change tracking detects the modification
            List<OriginalChapterPart> updatedParts = new(existingMergedInfo.OriginalParts);
            updatedParts.AddRange(mergeInfo.OriginalParts);
            existingMergedInfo.OriginalParts = updatedParts;

            // Remove the new chapter part records from the database
            dbContext.Chapters.RemoveRange(newChapterParts);

            // Handle upscale task management for the new parts (cancels any pending tasks)
            await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
                newChapterParts, mergeInfo, library, null, cancellationToken);

            // Handle upscaling for the existing merged chapter that now has new parts
            await HandleExistingMergedChapterUpscalingAsync(
                existingMergedChapter, library, cancellationToken);

            logger.LogInformation(
                "Successfully added {PartCount} new chapter parts to existing merged chapter {BaseNumber} for series {SeriesTitle}",
                mergeInfo.OriginalParts.Count, mergeInfo.BaseChapterNumber, manga.PrimaryTitle);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to add parts to existing merged chapter {BaseNumber} for series {SeriesTitle}",
                mergeInfo.BaseChapterNumber, manga.PrimaryTitle);
        }
    }

    private async Task AddPartsToExistingMergedChapterAsync(
        Chapter existingMergedChapter,
        List<OriginalChapterPart> newParts,
        string seriesLibraryPath,
        CancellationToken cancellationToken)
    {
        string existingMergedFilePath = Path.Combine(seriesLibraryPath, existingMergedChapter.FileName);

        if (!File.Exists(existingMergedFilePath))
        {
            throw new InvalidOperationException(
                $"Existing merged chapter file not found: {existingMergedFilePath}");
        }

        // Create a temporary file for the updated merged chapter
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"manga_merge_addition_{Guid.NewGuid():N}");
        string tempMergedFilePath = Path.Combine(tempDirectory, existingMergedChapter.FileName);

        try
        {
            Directory.CreateDirectory(tempDirectory);

            // Copy the existing merged chapter to temp location for modification
            File.Copy(existingMergedFilePath, tempMergedFilePath);

            // Add the new parts to the merged chapter
            await using (ZipArchive existingArchive =
                         await ZipFile.OpenAsync(tempMergedFilePath, ZipArchiveMode.Update, cancellationToken))
            {
                // Find the current highest page number in the existing archive
                int currentMaxPageNumber = 0;
                foreach (ZipArchiveEntry entry in existingArchive.Entries)
                {
                    if (IsImageFile(entry.Name))
                    {
                        string nameWithoutExtension = Path.GetFileNameWithoutExtension(entry.Name);
                        if (int.TryParse(nameWithoutExtension, out int pageNumber))
                        {
                            currentMaxPageNumber = Math.Max(currentMaxPageNumber, pageNumber);
                        }
                    }
                }

                int pageCounter = currentMaxPageNumber + 1;

                // Add pages from each new part
                foreach (OriginalChapterPart newPart in newParts)
                {
                    string newPartFilePath = Path.Combine(seriesLibraryPath, newPart.FileName);

                    if (!File.Exists(newPartFilePath))
                    {
                        logger.LogWarning("New part file not found: {FilePath}", newPartFilePath);
                        continue;
                    }

                    // Update the page indices for the new part
                    newPart.StartPageIndex = pageCounter;

                    await using (ZipArchive newPartArchive =
                                 await ZipFile.OpenReadAsync(newPartFilePath, cancellationToken))
                    {
                        List<ZipArchiveEntry> imageEntries = newPartArchive.Entries
                            .Where(e => IsImageFile(e.Name) && !e.FullName.EndsWith("/"))
                            .Select(e => new { Entry = e, Name = e.Name })
                            .OrderBy(x => x.Name, new NaturalSortComparer<string>(s => s))
                            .Select(x => x.Entry)
                            .ToList();

                        foreach (ZipArchiveEntry entry in imageEntries)
                        {
                            string newPageName = $"{pageCounter:D4}{Path.GetExtension(entry.Name)}";

                            ZipArchiveEntry newEntry = existingArchive.CreateEntry(newPageName);
                            await using (Stream originalStream = await entry.OpenAsync(cancellationToken))
                            await using (Stream newStream = await newEntry.OpenAsync(cancellationToken))
                            {
                                await originalStream.CopyToAsync(newStream, cancellationToken);
                            }

                            pageCounter++;
                        }
                    }

                    newPart.EndPageIndex = pageCounter - 1;

                    // Delete the original new part file
                    try
                    {
                        File.Delete(newPartFilePath);
                        logger.LogInformation("Deleted original new part file: {FilePath}", newPartFilePath);
                    }
                    catch (Exception deleteEx)
                    {
                        logger.LogError(deleteEx, "Failed to delete original new part file: {FilePath}",
                            newPartFilePath);
                    }
                }
            }

            // Replace the original merged file with the updated one
            File.Move(tempMergedFilePath, existingMergedFilePath, true);
            logger.LogDebug("Updated existing merged file: {FilePath}", existingMergedFilePath);
        }
        finally
        {
            // Clean up temporary directory
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to clean up temporary directory: {TempDirectory}", tempDirectory);
            }
        }
    }

    private async Task HandleUpscaledChapterAdditionAsync(
        List<Chapter> newChapterParts,
        MergeInfo mergeInfo,
        Library library,
        string seriesLibraryPath,
        Chapter existingMergedChapter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath))
        {
            return; // No upscaled library configured
        }

        string upscaledSeriesPath = Path.Combine(
            library.UpscaledLibraryPath,
            PathEscaper.EscapeFileName(existingMergedChapter.Manga.PrimaryTitle!));

        string existingUpscaledMergedFile = Path.Combine(upscaledSeriesPath, existingMergedChapter.FileName);

        if (!File.Exists(existingUpscaledMergedFile))
        {
            logger.LogDebug("No existing upscaled merged file found at {FilePath}, skipping upscaled addition",
                existingUpscaledMergedFile);
            return;
        }

        // Check if upscaled versions exist for all new parts
        var upscaledNewParts = new List<FoundChapter>();
        bool allPartsHaveUpscaledVersions = true;

        foreach (OriginalChapterPart newPart in mergeInfo.OriginalParts)
        {
            string upscaledPartFilePath = Path.Combine(upscaledSeriesPath, newPart.FileName);

            if (File.Exists(upscaledPartFilePath))
            {
                upscaledNewParts.Add(new FoundChapter(
                    newPart.FileName,
                    Path.GetRelativePath(library.UpscaledLibraryPath, upscaledPartFilePath),
                    ChapterStorageType.Cbz,
                    newPart.Metadata));
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
                "Not all new parts have upscaled versions for base number {BaseNumber}, skipping upscaled addition",
                mergeInfo.BaseChapterNumber);
            return;
        }

        try
        {
            // Add the upscaled parts to the existing upscaled merged file
            await AddPartsToExistingMergedChapterAsync(
                existingMergedChapter,
                mergeInfo.OriginalParts,
                upscaledSeriesPath,
                cancellationToken);

            logger.LogInformation(
                "Successfully added {PartCount} new upscaled parts to existing upscaled merged chapter {BaseNumber}",
                upscaledNewParts.Count, mergeInfo.BaseChapterNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to add upscaled parts to existing upscaled merged chapter {BaseNumber}",
                mergeInfo.BaseChapterNumber);
        }
    }

    /// <summary>
    /// Handles upscaling for an existing merged chapter when new parts are added to it.
    /// This marks the chapter as not upscaled and queues a new upscale task if needed.
    /// </summary>
    private async Task HandleExistingMergedChapterUpscalingAsync(
        Chapter existingMergedChapter,
        Library library,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(library.UpscaledLibraryPath) || library.UpscalerProfile is null)
        {
            return;
        }

        // Load necessary references
        await dbContext.Entry(existingMergedChapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
        await dbContext.Entry(existingMergedChapter.Manga).Reference(m => m.Library).LoadAsync(cancellationToken);
        await dbContext.Entry(existingMergedChapter.Manga.Library).Reference(l => l.UpscalerProfile)
            .LoadAsync(cancellationToken);

        // Check if the manga should be upscaled
        bool shouldUpscale = (existingMergedChapter.Manga.ShouldUpscale ??
                              existingMergedChapter.Manga.Library.UpscaleOnIngest)
                             && existingMergedChapter.Manga.Library.UpscalerProfile != null;

        if (!shouldUpscale)
        {
            logger.LogDebug(
                "Upscaling not needed for existing merged chapter {FileName} - upscaling disabled",
                existingMergedChapter.FileName);
            return;
        }

        // Cancel any existing upscale tasks for this chapter
        List<PersistedTask> existingTasks = await dbContext.PersistedTasks
            .FromSql(
                $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {existingMergedChapter.Id}")
            .ToListAsync(cancellationToken);

        foreach (PersistedTask task in existingTasks)
        {
            await taskQueue.RemoveTaskAsync(task);
            logger.LogDebug("Canceled existing upscale task for merged chapter {ChapterId} due to new parts addition",
                existingMergedChapter.Id);
        }

        // Mark the chapter as not upscaled since new parts were added
        existingMergedChapter.IsUpscaled = false;

        // Remove any existing upscaled version since it's now outdated
        string upscaledSeriesPath = Path.Combine(
            library.UpscaledLibraryPath,
            PathEscaper.EscapeFileName(existingMergedChapter.Manga.PrimaryTitle!));
        string upscaledChapterPath = Path.Combine(upscaledSeriesPath, existingMergedChapter.FileName);

        if (File.Exists(upscaledChapterPath))
        {
            try
            {
                File.Delete(upscaledChapterPath);
                logger.LogInformation(
                    "Removed outdated upscaled version of merged chapter {FileName} due to new parts addition",
                    existingMergedChapter.FileName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to remove outdated upscaled version of merged chapter {FileName}",
                    existingMergedChapter.FileName);
            }
        }

        // Queue a new upscale task for the updated merged chapter
        var upscaleTask = new UpscaleTask(existingMergedChapter);
        await taskQueue.EnqueueAsync(upscaleTask);

        logger.LogInformation(
            "Queued new upscale task for existing merged chapter {FileName} (Chapter ID: {ChapterId}) after adding new parts",
            existingMergedChapter.FileName, existingMergedChapter.Id);
    }

    private static bool IsImageFile(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".avif";
    }

    #region Helper Methods for Code Deduplication

    /// <summary>
    /// Loads necessary Entity Framework references for chapters and their manga/library.
    /// </summary>
    private async Task LoadChapterReferencesAsync(List<Chapter> chapters, CancellationToken cancellationToken)
    {
        foreach (Chapter chapter in chapters)
        {
            if (!dbContext.Entry(chapter).Reference(c => c.Manga).IsLoaded)
            {
                await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync(cancellationToken);
            }
        }

        // Load library and chapters collection reference for the first chapter's manga (all chapters should be from same manga)
        if (chapters.Any())
        {
            Manga manga = chapters.First().Manga;
            if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
            {
                await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
            }

            // **CRITICAL FIX**: Load the Manga.Chapters collection so we have access to all chapters
            if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
            {
                await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync(cancellationToken);
                logger.LogDebug("LoadChapterReferencesAsync: Loaded {ChapterCount} chapters for manga {MangaId}",
                    manga.Chapters.Count, manga.Id);
            }
        }
    }

    /// <summary>
    /// Converts Chapter entities to FoundChapter objects for processing.
    /// </summary>
    private async Task<List<FoundChapter>> ConvertChaptersToFoundChapters(List<Chapter> chapters)
    {
        List<FoundChapter> foundChapters = new(chapters.Count);
        foreach (Chapter chapter in chapters)
        {
            ExtractedMetadata metadata = await GetChapterMetadata(chapter);
            foundChapters.Add(new FoundChapter(
                chapter.FileName,
                Path.GetFileName(chapter.RelativePath),
                ChapterStorageType.Cbz,
                metadata));
        }

        return foundChapters;
    }

    /// <summary>
    /// Builds the series library path for a manga.
    /// </summary>
    private static string BuildSeriesLibraryPath(Library library, string mangaTitle)
    {
        return Path.Combine(
            library.NotUpscaledLibraryPath,
            PathEscaper.EscapeFileName(mangaTitle));
    }

    /// <summary>
    /// Creates a function to check if a chapter number represents the latest chapter.
    /// </summary>
    private static Func<string, bool> CreateLatestChapterChecker(HashSet<string> allChapterNumbers,
        bool includeLatestChapters)
    {
        return baseNumber => !includeLatestChapters && IsLatestChapterStatic(baseNumber, allChapterNumbers);
    }

    /// <summary>
    /// Static version of IsLatestChapter for use in delegates.
    /// </summary>
    private static bool IsLatestChapterStatic(string baseNumber, HashSet<string> allChapterNumbers)
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

    /// <summary>
    /// Processes a single merge group with common merge operations.
    /// </summary>
    private async Task ProcessSingleMergeGroupAsync(
        MergeInfo mergeInfo,
        List<Chapter> originalChapters,
        Library library,
        string seriesLibraryPath,
        CancellationToken cancellationToken)
    {
        // Check upscale compatibility
        UpscaleCompatibilityResult compatibility =
            await upscaleTaskManager.CheckUpscaleCompatibilityForMergeAsync(
                originalChapters, cancellationToken);

        if (!compatibility.CanMerge)
        {
            logger.LogInformation(
                "Skipping merge of chapter parts for {MergedFileName}: {Reason}",
                mergeInfo.MergedChapter.FileName, compatibility.Reason);
            return;
        }

        // Handle merging of upscaled versions if they exist
        UpscaledMergeResult upscaledMergeResult = await HandleUpscaledChapterMergingAsync(
            originalChapters, mergeInfo, library, seriesLibraryPath, cancellationToken);

        // Update database records to reflect the merge
        await UpdateDatabaseForMergeAsync(mergeInfo, originalChapters, cancellationToken);

        // Handle upscale task management with information about partial merging
        await upscaleTaskManager.HandleUpscaleTaskManagementAsync(
            originalChapters, mergeInfo, library, upscaledMergeResult, cancellationToken);
    }

    #endregion
}