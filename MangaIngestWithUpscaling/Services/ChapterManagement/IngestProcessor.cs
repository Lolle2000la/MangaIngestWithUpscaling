using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

[RegisterScoped]
public partial class IngestProcessor(
    ApplicationDbContext dbContext,
    IChapterInIngestRecognitionService chapterRecognitionService,
    ILibraryRenamingService renamingService,
    ICbzConverter cbzConverter,
    ILogger<IngestProcessor> logger,
    ITaskQueue taskQueue,
    IMetadataHandlingService metadataHandling,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IChapterPartMerger chapterPartMerger,
    IChapterMergeCoordinator chapterMergeCoordinator,
    UpscaleTaskProcessor upscaleTaskProcessor,
    IImageFilterService imageFilterService
) : IIngestProcessor
{
    public async Task ProcessAsync(Library library, CancellationToken cancellationToken)
    {
        if (!dbContext.Entry(library).Reference(l => l.UpscalerProfile).IsLoaded)
        {
            await dbContext.Entry(library).Reference(l => l.UpscalerProfile).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(library).Collection(l => l.FilterRules).IsLoaded)
        {
            await dbContext.Entry(library).Collection(l => l.FilterRules).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(library).Collection(l => l.RenameRules).IsLoaded)
        {
            await dbContext.Entry(library).Collection(l => l.RenameRules).LoadAsync(cancellationToken);
        }

        if (!dbContext.Entry(library).Collection(l => l.FilteredImages).IsLoaded)
        {
            await dbContext.Entry(library).Collection(l => l.FilteredImages).LoadAsync(cancellationToken);
        }

        var foundChapters = chapterRecognitionService.FindAllChaptersAt(
            library.IngestPath, library.FilterRules, cancellationToken);

        // preserve original series for alternative title
        Dictionary<string, string> originalSeriesMap = await foundChapters.ToDictionaryAsync(c => c.RelativePath,
            c => c.Metadata.Series,
            cancellationToken: cancellationToken);

        // apply rename rules and keep track of original and renamed versions
        var processedChapters = new List<ProcessedChapterInfo>();
        await foreach (FoundChapter originalChapter in foundChapters)
        {
            FoundChapter renamedChapter = renamingService.ApplyRenameRules(originalChapter, library.RenameRules);
            bool isUpscaled = IsUpscaledChapter().IsMatch(originalChapter.RelativePath);
            UpscalerProfileJsonDto? upscalerProfileDto = null;

            string fullPath = Path.Combine(library.IngestPath, originalChapter.RelativePath);
            upscalerProfileDto =
                await upscalerJsonHandlingService.ReadUpscalerJsonAsync(fullPath, cancellationToken);
            if (upscalerProfileDto != null)
            {
                isUpscaled = true;
            }

            processedChapters.Add(new ProcessedChapterInfo(originalChapter, renamedChapter, isUpscaled,
                upscalerProfileDto));
        }

        // group chapters by new series title
        var chaptersBySeries = processedChapters
            .GroupBy(pci => pci.Renamed.Metadata.Series)
            .ToDictionary(g => g.Key,
                // Sort chapters so that non-upscaled chapters come before upscaled ones
                // By processing the non-upscaled chapters first, we ensure that if we also encounter a matching upscaled
                // chapter, we can associate it with the correct non-upscaled chapter.
                g => g.OrderBy(pci => pci.IsUpscaled).ToList());

        List<Chapter> chaptersToUpscale = new();
        List<Task> scans = new();
        var upscalerProfileCache = new Dictionary<UpscalerProfileJsonDto, UpscalerProfile>();
        var processedSeriesEntities = new List<Manga>(); // Track processed series for retroactive merging

        foreach (var (series, processedItems) in chaptersBySeries)
        {
            var firstOriginalRelativePath = processedItems.First().Original.RelativePath;
            var originalSeriesTitle = originalSeriesMap[firstOriginalRelativePath];
            Manga seriesEntity = await GetMangaSeriesEntity(library, series, originalSeriesTitle, cancellationToken);
            processedSeriesEntities.Add(seriesEntity); // Track this series

            // Check if chapter merging is enabled for this library/series
            bool shouldMergeChapterParts = seriesEntity.MergeChapterParts ?? library.MergeChapterParts;

            List<ProcessedChapterInfo> finalProcessedItems = processedItems;
            ChapterMergeResult? mergeResult = null;

            // Apply chapter part merging if enabled
            if (shouldMergeChapterParts && chapterPartMerger != null)
            {
                // Get all chapter numbers including existing ones and new ones
                HashSet<string> existingChapterNumbers = seriesEntity.Chapters
                    .Select(c => ChapterNumberHelper.ExtractChapterNumber(c.FileName))
                    .Where(n => n != null)
                    .Cast<string>()
                    .ToHashSet();

                HashSet<string> newChapterNumbers = processedItems
                    .Where(p => !p.IsUpscaled)
                    .Select(p => ChapterNumberHelper.ExtractChapterNumber(p.Renamed.FileName))
                    .Where(n => n != null)
                    .Cast<string>()
                    .ToHashSet();

                HashSet<string> allChapterNumbers = existingChapterNumbers.Union(newChapterNumbers).ToHashSet();

                // Get base chapter numbers that already have merged chapters to prevent conflicts
                List<int> seriesChapterIds = seriesEntity.Chapters.Select(c => c.Id).ToList();
                HashSet<string> existingMergedBaseNumbers = await dbContext.MergedChapterInfos
                    .Where(m => seriesChapterIds.Contains(m.ChapterId))
                    .Select(m => m.MergedChapterNumber)
                    .ToHashSetAsync(cancellationToken);

                // Create a mapping function to get original file paths for renamed chapters
                Dictionary<string, string> originalPathLookup = processedItems
                    .Where(p => !p.IsUpscaled)
                    .ToDictionary(p => p.Renamed.RelativePath,
                        p => Path.Combine(library.IngestPath, p.Original.RelativePath));

                Func<FoundChapter, string> getActualFilePath = renamedChapter =>
                    originalPathLookup.TryGetValue(renamedChapter.RelativePath, out string? originalPath)
                        ? originalPath
                        : Path.Combine(library.IngestPath, renamedChapter.RelativePath);

                // Calculate the series directory path for merged chapter output
                string seriesDirectoryPath = Path.Combine(
                    library.NotUpscaledLibraryPath,
                    PathEscaper.EscapeFileName(seriesEntity.PrimaryTitle!));

                // Ensure the series directory exists
                fileSystem.CreateDirectory(seriesDirectoryPath);

                // Process chapter merging using the new simplified approach
                mergeResult = await chapterPartMerger.ProcessChapterMergingAsync(
                    processedItems.Where(p => !p.IsUpscaled).Select(p => p.Renamed).ToList(),
                    library.IngestPath,
                    seriesDirectoryPath, // Use series directory for merged chapter output
                    seriesEntity.PrimaryTitle!,
                    allChapterNumbers,
                    getActualFilePath,
                    cancellationToken);

                // Filter out merge results that would conflict with existing merged chapters
                if (mergeResult.MergeInformation.Any())
                {
                    List<MergeInfo> validMergeInfos = mergeResult.MergeInformation
                        .Where(mergeInfo => !existingMergedBaseNumbers.Contains(mergeInfo.BaseChapterNumber))
                        .ToList();

                    List<MergeInfo> conflictingMergeInfos = mergeResult.MergeInformation
                        .Where(mergeInfo => existingMergedBaseNumbers.Contains(mergeInfo.BaseChapterNumber))
                        .ToList();

                    if (conflictingMergeInfos.Any())
                    {
                        logger.LogWarning(
                            "Skipping {ConflictCount} merge operations for series {SeriesTitle} due to existing merged chapters with the same base numbers: {BaseNumbers}",
                            conflictingMergeInfos.Count,
                            seriesEntity.PrimaryTitle,
                            string.Join(", ", conflictingMergeInfos.Select(m => m.BaseChapterNumber)));

                        // Clean up any files that were created for the conflicting merges
                        foreach (MergeInfo conflictingMerge in conflictingMergeInfos)
                        {
                            string conflictingMergedFile = Path.Combine(seriesDirectoryPath,
                                conflictingMerge.MergedChapter.FileName);
                            if (File.Exists(conflictingMergedFile))
                            {
                                try
                                {
                                    File.Delete(conflictingMergedFile);
                                    logger.LogInformation("Deleted conflicting merged file: {FileName}",
                                        conflictingMergedFile);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Failed to delete conflicting merged file: {FileName}",
                                        conflictingMergedFile);
                                }
                            }
                        }
                    }

                    // Update merge result to only include valid merges
                    mergeResult = new ChapterMergeResult(
                        mergeResult.ProcessedChapters,
                        validMergeInfos);
                }

                // Convert merged chapters back to ProcessedChapterInfo format and add merge info
                var mergedProcessedItems = new List<ProcessedChapterInfo>();

                // Create a set of all original chapter parts that were successfully merged
                var mergedOriginalPartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MergeInfo mergeInfo in mergeResult.MergeInformation)
                {
                    foreach (OriginalChapterPart originalPart in mergeInfo.OriginalParts)
                    {
                        mergedOriginalPartPaths.Add(originalPart.FileName);
                    }
                }

                logger.LogInformation(
                    "Merge completed: {MergedCount} merged chapters created, {OriginalPartsCount} original parts should be excluded from processing",
                    mergeResult.MergeInformation.Count, mergedOriginalPartPaths.Count);

                // NEW: Cancel and remove any existing upscale tasks for original parts that were merged
                // This ensures we don't waste work on chapters that are no longer relevant after merge
                if (mergedOriginalPartPaths.Count != 0 && seriesEntity.Chapters.Any())
                {
                    List<Chapter> originalsForMerge = seriesEntity.Chapters
                        .Where(c => mergedOriginalPartPaths.Contains(c.FileName))
                        .ToList();

                    if (originalsForMerge.Count != 0)
                    {
                        await CancelUpscaleTasksForOriginalPartsAsync(originalsForMerge, cancellationToken);
                    }
                }

                // Add only chapters that were NOT merged into new files
                foreach (FoundChapter processedChapter in mergeResult.ProcessedChapters)
                {
                    // Check if this chapter is one of the merged chapters (newly created)
                    bool isMergedChapter = mergeResult.MergeInformation
                        .Any(mi => mi.MergedChapter.RelativePath == processedChapter.RelativePath);

                    if (!isMergedChapter)
                    {
                        // This is either a non-merged chapter or an original part that wasn't successfully merged
                        // Check if it's an original part that was supposed to be merged
                        bool wasSupposedToBeMerged = mergedOriginalPartPaths.Contains(processedChapter.FileName);

                        if (!wasSupposedToBeMerged)
                        {
                            // This is a regular chapter that wasn't involved in merging, add it for normal processing
                            ProcessedChapterInfo? originalProcessedItem = processedItems
                                .FirstOrDefault(p => p.Renamed.RelativePath == processedChapter.RelativePath);

                            if (originalProcessedItem != null)
                            {
                                mergedProcessedItems.Add(originalProcessedItem);
                            }
                        }
                        else
                        {
                            // This is an original part that was supposed to be merged but somehow is still here
                            // This should not happen if merging was successful, log a warning
                            logger.LogWarning(
                                "Chapter part {ChapterFile} was supposed to be merged but is still in processed chapters. " +
                                "This may indicate a merge operation that partially failed.",
                                processedChapter.FileName);
                        }
                    }
                    // Note: We don't add merged chapters (isMergedChapter == true) here as they are already 
                    // in their final location and don't need further file processing
                }

                // Add upscaled chapters back (they weren't processed for merging)
                mergedProcessedItems.AddRange(processedItems.Where(p => p.IsUpscaled));

                // Store merge information for later database operations
                foreach (MergeInfo mergeInfo in mergeResult.MergeInformation)
                {
                    // We'll handle database operations later in the processing pipeline
                    // For now, just log the successful merge
                    logger.LogInformation(
                        "Prepared merge info for chapter {MergedFileName} from {PartCount} parts",
                        mergeInfo.MergedChapter.FileName, mergeInfo.OriginalParts.Count);
                }

                finalProcessedItems = mergedProcessedItems;
            }

            // Move chapters to the target path in file system as specified by the libraries NotUpscaledLibraryPath property.
            // Then create a Chapter entity for each chapter and add it to the series.
            foreach (ProcessedChapterInfo pci in finalProcessedItems)
            {
                var originalChapter = pci.Original;
                var renamedChapter = pci.Renamed;

                // Check if this chapter part has already been merged and skip if so
                if (await chapterMergeCoordinator.IsChapterPartAlreadyMergedAsync(renamedChapter.FileName, seriesEntity,
                        cancellationToken))
                {
                    logger.LogInformation(
                        "Skipping ingestion of chapter {ChapterFileName} for series {SeriesTitle} - this chapter part has already been merged into another chapter",
                        renamedChapter.FileName, seriesEntity.PrimaryTitle);
                    continue;
                }

                // if this is an already upscaled chapter, go a different route
                if (pci.IsUpscaled) // Check original path for _upscaled marker
                {
                    if (!originalChapter.RelativePath.EndsWith(".cbz")) // Check original path for .cbz
                    {
                        logger.LogError(
                            "Upscaled chapter {chapter} is not a cbz file. At this moment only cbz-files are supported for ingest of already existing files.",
                            originalChapter.RelativePath);
                        continue;
                    }

                    // Pass both original and renamed info
                    Chapter? foundMatch =
                        await IngestUpscaledChapterIfMatchFound(originalChapter, renamedChapter, seriesEntity, library,
                            pci.UpscalerProfile, upscalerProfileCache, cancellationToken);
                    if (foundMatch != null)
                    {
                        int chapterIndex = chaptersToUpscale.FindIndex(c => c.Id == foundMatch.Id);
                        if (chapterIndex != -1)
                            chaptersToUpscale.RemoveAt(chapterIndex);
                    }

                    continue;
                }

                FoundChapter convertedChapter;
                var convertedChapterPath = string.Empty;
                try
                {
                    convertedChapter = cbzConverter.ConvertToCbz(originalChapter, library.IngestPath);
                    convertedChapterPath = Path.Combine(library.IngestPath, convertedChapter.RelativePath);

                    // Update metadata if needed
                    var desiredMeta = renamedChapter.Metadata;
                    var currentMeta = originalChapter.Metadata;

                    // Make sure we use the primary title from the database entity
                    if (desiredMeta.Series != seriesEntity.PrimaryTitle)
                    {
                        desiredMeta = desiredMeta with { Series = seriesEntity.PrimaryTitle! };
                    }

                    if (!currentMeta.Equals(desiredMeta))
                    {
                        logger.LogInformation("Updating metadata for {Path}. Old: {Old}, New: {New}",
                            convertedChapterPath, currentMeta, desiredMeta);
                        await metadataHandling.WriteComicInfoAsync(convertedChapterPath, desiredMeta);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error converting or updating metadata for {Path}",
                        originalChapter.RelativePath);
                    continue;
                }

                var targetPath = Path.Combine(
                    library.NotUpscaledLibraryPath,
                    PathEscaper.EscapeFileName(seriesEntity.PrimaryTitle!),
                    PathEscaper.EscapeFileName(renamedChapter.FileName));

                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {File} exists at {Target}. Skipping.", renamedChapter.FileName,
                        targetPath);
                    continue;
                }

                fileSystem.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                var relativeDbPath = Path.GetRelativePath(library.NotUpscaledLibraryPath, targetPath);
                if (await dbContext.Chapters.AnyAsync(
                        c => c.RelativePath == relativeDbPath && c.Manga.Id == seriesEntity.Id, cancellationToken))
                {
                    logger.LogWarning("Chapter {File} already in DB. Skipping.", renamedChapter.FileName);
                    continue;
                }

                // Move converted file and add chapter entity
                var chapterEntity = new Chapter
                {
                    FileName = renamedChapter.FileName,
                    Manga = seriesEntity,
                    MangaId = seriesEntity.Id,
                    RelativePath = relativeDbPath,
                    IsUpscaled = false
                };
                fileSystem.Move(convertedChapterPath, targetPath);

                // Apply image filters if configured
                if (library.FilteredImages.Any())
                {
                    try
                    {
                        var filterResult =
                            await imageFilterService.ApplyFiltersToChapterAsync(targetPath, library.FilteredImages,
                                cancellationToken);
                        if (filterResult.FilteredCount > 0)
                        {
                            logger.LogInformation("Filtered {Count} images from chapter {FileName} during ingest",
                                filterResult.FilteredCount, chapterEntity.FileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to apply image filters to {TargetPath}", targetPath);
                    }
                }

                seriesEntity.Chapters.Add(chapterEntity);

                // Check if this chapter has merge information
                if (renamedChapter.Metadata.ChapterTitle?.Contains("__MERGE_INFO__") == true)
                {
                    string[] parts = renamedChapter.Metadata.ChapterTitle.Split("__MERGE_INFO__");
                    if (parts.Length == 2)
                    {
                        try
                        {
                            var mergeInfo = JsonSerializer.Deserialize<MergedChapterInfo>(parts[1]);
                            if (mergeInfo != null)
                            {
                                mergeInfo.ChapterId = chapterEntity.Id;
                                mergeInfo.Chapter = chapterEntity;
                                dbContext.MergedChapterInfos.Add(mergeInfo);

                                // Clean up the chapter title
                                ExtractedMetadata cleanMetadata = renamedChapter.Metadata with
                                {
                                    ChapterTitle = parts[0]
                                };
                                await metadataHandling.WriteComicInfoAsync(targetPath, cleanMetadata);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to save merge information for chapter {ChapterFile}",
                                renamedChapter.FileName);
                        }
                    }
                }

                scans.Add(chapterChangedNotifier.Notify(chapterEntity, false));

                if (library.UpscaleOnIngest && seriesEntity.ShouldUpscale != false &&
                    library.UpscalerProfileId is not null)
                {
                    dbContext.Entry(chapterEntity).Reference(c => c.UpscalerProfile).Load();
                    chaptersToUpscale.Add(chapterEntity);
                }
            }

            // Process merged chapters - add them to the database
            if (mergeResult != null)
            {
                foreach (MergeInfo mergeInfo in mergeResult.MergeInformation)
                {
                    try
                    {
                        // Create chapter entity for the merged chapter
                        FoundChapter mergedChapter = mergeInfo.MergedChapter;
                        string seriesDirectoryName = PathEscaper.EscapeFileName(series);
                        string correctedRelativePath = Path.Combine(seriesDirectoryName, mergedChapter.FileName);
                        string mergedTargetPath = Path.Combine(library.NotUpscaledLibraryPath, correctedRelativePath);

                        var mergedChapterEntity = new Chapter
                        {
                            FileName = mergedChapter.FileName,
                            Manga = seriesEntity,
                            MangaId = seriesEntity.Id,
                            RelativePath = correctedRelativePath,
                            IsUpscaled = false
                        };

                        // Verify that the merged chapter file exists at the target location
                        if (!File.Exists(mergedTargetPath))
                        {
                            throw new InvalidOperationException(
                                $"Merged chapter file {mergedChapter.FileName} was not found at expected location: {mergedTargetPath}");
                        }

                        logger.LogDebug("Merged chapter file verified at: {MergedTargetPath}", mergedTargetPath);

                        seriesEntity.Chapters.Add(mergedChapterEntity);

                        // Create and save merge info to database
                        var mergedChapterInfo = new MergedChapterInfo
                        {
                            ChapterId = mergedChapterEntity.Id,
                            Chapter = mergedChapterEntity,
                            OriginalParts = mergeInfo.OriginalParts,
                            MergedChapterNumber = mergeInfo.BaseChapterNumber,
                            CreatedAt = DateTime.UtcNow
                        };

                        dbContext.MergedChapterInfos.Add(mergedChapterInfo);

                        // Notify about the new merged chapter
                        scans.Add(chapterChangedNotifier.Notify(mergedChapterEntity, false));

                        // Add to upscale queue if needed
                        if (library.UpscaleOnIngest && seriesEntity.ShouldUpscale != false &&
                            library.UpscalerProfileId is not null)
                        {
                            dbContext.Entry(mergedChapterEntity).Reference(c => c.UpscalerProfile).Load();
                            chaptersToUpscale.Add(mergedChapterEntity);
                        }

                        logger.LogInformation(
                            "Added merged chapter {MergedFileName} to database from {PartCount} original parts",
                            mergedChapter.FileName, mergeInfo.OriginalParts.Count);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to add merged chapter {MergedFileName} to database",
                            mergeInfo.MergedChapter.FileName);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var chapterTuple in chaptersToUpscale)
        {
            var upscaleTask =
                new UpscaleTask(chapterTuple);
            await taskQueue.EnqueueAsync(upscaleTask);
        }

        await Task.WhenAll(scans);

        // Check for existing chapter parts that can now be merged retroactively
        foreach (Manga seriesEntity in processedSeriesEntities)
        {
            await chapterMergeCoordinator.ProcessExistingChapterPartsForMergingAsync(seriesEntity, cancellationToken);
        }

        logger.LogInformation("Scanned {seriesCount} series in library {libraryName}. Cleaning.",
            chaptersBySeries.Count, library.Name);
        // Clean the ingest path of all empty directories recursively
        FileSystemHelpers.DeleteEmptySubfolders(library.IngestPath, logger);
    }

    /// <summary>
    ///     Cancel and remove any existing UpscaleTasks for the given original chapter parts.
    ///     Pending tasks are removed directly; processing tasks are canceled via the processor and then removed.
    ///     Completed/failed/canceled tasks are cleaned up.
    /// </summary>
    private async Task CancelUpscaleTasksForOriginalPartsAsync(List<Chapter> originals,
        CancellationToken cancellationToken)
    {
        foreach (Chapter ch in originals)
        {
            List<PersistedTask> tasks = await dbContext.PersistedTasks
                .FromSql(
                    $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {ch.Id}")
                .ToListAsync(cancellationToken);

            foreach (PersistedTask task in tasks)
            {
                switch (task.Status)
                {
                    case PersistedTaskStatus.Pending:
                        // Remove pending tasks
                        upscaleTaskProcessor.CancelCurrent(task);
                        await taskQueue.RemoveTaskAsync(task);
                        logger.LogInformation(
                            "Removed pending upscale task for original chapter {ChapterId} due to merge", ch.Id);
                        break;

                    case PersistedTaskStatus.Processing:
                        // Cancel and then remove processing tasks
                        upscaleTaskProcessor.CancelCurrent(task);
                        // Give processor a brief moment and refresh status
                        await Task.Delay(50, cancellationToken);
                        try { await dbContext.Entry(task).ReloadAsync(cancellationToken); }
                        catch
                        {
                            /* ignore */
                        }

                        await taskQueue.RemoveTaskAsync(task);
                        logger.LogInformation(
                            "Canceled and removed processing upscale task for original chapter {ChapterId} due to merge",
                            ch.Id);
                        break;

                    default:
                        // Clean up completed/failed/canceled
                        await taskQueue.RemoveTaskAsync(task);
                        logger.LogDebug(
                            "Cleaned up {Status} upscale task for original chapter {ChapterId} due to merge",
                            task.Status, ch.Id);
                        break;
                }
            }
        }
    }

    private async Task<Manga> GetMangaSeriesEntity(Library library, string newSeries, string originalSeries,
        CancellationToken cancellationToken)
    {
        // create series if it doesn't exist
        // Also take into account the alternate names
        var seriesEntity = await dbContext.MangaSeries
            .Include(s => s.OtherTitles)
            .Include(s => s.Chapters)
            .Include(s => s.UpscalerProfilePreference)
            .FirstOrDefaultAsync(s => s.PrimaryTitle == newSeries || s.OtherTitles.Any(an => an.Title == newSeries),
                cancellationToken: cancellationToken);

        if (seriesEntity == null)
        {
            seriesEntity = new Manga
            {
                PrimaryTitle = newSeries,
                OtherTitles = new List<MangaAlternativeTitle>(),
                Library = library,
                LibraryId = library.Id,
                Chapters = new List<Chapter>()
            };
            // add original as alternative title if changed
            if (!string.Equals(originalSeries, newSeries, StringComparison.Ordinal))
            {
                seriesEntity.OtherTitles.Add(new MangaAlternativeTitle
                {
                    Manga = seriesEntity, Title = originalSeries
                });
            }

            dbContext.MangaSeries.Add(seriesEntity);
        }

        if (seriesEntity.Chapters == null)
        {
            seriesEntity.Chapters = new List<Chapter>();
        }

        return seriesEntity;
    }

    private async Task<Chapter?> IngestUpscaledChapterIfMatchFound(FoundChapter originalUpscaled,
        FoundChapter renamedUpscaled,
        Manga seriesEntity, Library library, UpscalerProfileJsonDto? upscalerProfileDto,
        Dictionary<UpscalerProfileJsonDto, UpscalerProfile> upscalerProfileCache, CancellationToken cancellationToken)
    {
        // Path to the upscaled CBZ file in the ingest directory (using original path info)
        var cbzPath = Path.Combine(library.IngestPath, originalUpscaled.RelativePath);

        // Check if the source file actually exists before trying to process it
        if (!File.Exists(cbzPath))
        {
            logger.LogError(
                "Source upscaled chapter file not found at {path}. Original RelativePath: {origPath}, Renamed RelativePath: {renamedPath}. Skipping.",
                cbzPath, originalUpscaled.RelativePath, renamedUpscaled.RelativePath);
            return null;
        }

        // find the non-upscaled chapter that matches the found upscaled chapter
        // Match using renamed information as that's what the user expects to align with library state
        var nonUpscaledChapter = seriesEntity.Chapters.FirstOrDefault(c =>
            c.FileName == renamedUpscaled.FileName ||
            c.FileName == PathEscaper.EscapeFileName(renamedUpscaled.FileName));

        // Corrected: Use a foreach loop to await the asynchronous predicate
        if (nonUpscaledChapter == null && !string.IsNullOrEmpty(renamedUpscaled.Metadata.ChapterTitle))
        {
            foreach (Chapter chapter in seriesEntity.Chapters)
            {
                try
                {
                    // Path to the existing non-upscaled chapter file in the library
                    string existingNonUpscaledFilePath =
                        Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
                    if (!File.Exists(existingNonUpscaledFilePath))
                    {
                        continue;
                    }

                    ExtractedMetadata existingMetadata =
                        await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(existingNonUpscaledFilePath);
                    if (existingMetadata.ChapterTitle == renamedUpscaled.Metadata.ChapterTitle)
                    {
                        nonUpscaledChapter = chapter;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error reading metadata for existing chapter {chapterPath} ({chapterId}) during upscaled matching.",
                        Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath), chapter.Id);
                    continue;
                }
            }
        }

        if (nonUpscaledChapter == null)
        {
            logger.LogWarning(
                "Upscaled chapter {FoundFileName} (Original: {OriginalIngestPath}) does not have a matching non-upscaled chapter in series {MangaTitle} ({MangaId}). Skipping.",
                renamedUpscaled.FileName, originalUpscaled.RelativePath, seriesEntity.PrimaryTitle, seriesEntity.Id);
            return null;
        }

        UpscalerProfile? profileToUse = null;
        if (upscalerProfileDto != null)
        {
            if (upscalerProfileCache.TryGetValue(upscalerProfileDto, out UpscalerProfile? cachedProfile))
            {
                profileToUse = cachedProfile;
            }
            else
            {
                var scaleFactor = (ScaleFactor)upscalerProfileDto.ScalingFactor;
                profileToUse = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(p =>
                        p.Name.ToLower() == upscalerProfileDto.Name.ToLower() &&
                        p.UpscalerMethod == upscalerProfileDto.UpscalerMethod &&
                        p.ScalingFactor == scaleFactor &&
                        p.CompressionFormat == upscalerProfileDto.CompressionFormat &&
                        p.Quality == upscalerProfileDto.Quality,
                    cancellationToken
                );

                if (profileToUse == null)
                {
                    profileToUse = new UpscalerProfile
                    {
                        Name = upscalerProfileDto.Name,
                        UpscalerMethod = upscalerProfileDto.UpscalerMethod,
                        ScalingFactor = scaleFactor,
                        CompressionFormat = upscalerProfileDto.CompressionFormat,
                        Quality = upscalerProfileDto.Quality
                    };
                    dbContext.UpscalerProfiles.Add(profileToUse);
                }

                upscalerProfileCache[upscalerProfileDto] = profileToUse;
            }
        }

        try
        {
            var metadataInFile = originalUpscaled.Metadata;
            ExtractedMetadata finalDesiredMetadata =
                renamedUpscaled.Metadata with { Series = seriesEntity.PrimaryTitle };

            if (!metadataInFile.Equals(finalDesiredMetadata))
            {
                logger.LogInformation(
                    "Metadata for upscaled chapter {ChapterPath} changed. Writing new metadata. Old: {OldMetadata}, New: {NewMetadata}",
                    cbzPath, metadataInFile, finalDesiredMetadata);
                await metadataHandling.WriteComicInfoAsync(cbzPath, finalDesiredMetadata);
            }

            // Apply image filters to the upscaled chapter if configured
            if (library.FilteredImages.Any())
            {
                try
                {
                    // For upscaled chapters, we need to match by base filename since extensions might change
                    var filterResult =
                        await imageFilterService.ApplyFiltersToChapterAsync(cbzPath, library.FilteredImages,
                            cancellationToken);
                    if (filterResult.FilteredCount > 0)
                    {
                        logger.LogInformation("Filtered {Count} images from upscaled chapter {FileName} during ingest",
                            filterResult.FilteredCount, originalUpscaled.FileName);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to apply image filters to upscaled chapter {CbzPath}", cbzPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error reading or updating metadata for upscaled chapter {chapterPath}. Original ingest path: {origIngestPath}. Skipping move.",
                cbzPath, originalUpscaled.RelativePath);
            return null; // If metadata handling fails, don't proceed to move
        }

        if (library.UpscaledLibraryPath == null)
        {
            logger.LogWarning(
                "Found matching upscaled chapter {RenamedFileName} (Original: {OriginalIngestPath}), but no path is set for the upscale-folder in library {LibraryName}. Metadata updated, but file not moved.",
                renamedUpscaled.FileName, originalUpscaled.RelativePath, library.Name);
            return null;
        }

        var upscaleTargetFolder = Path.Combine(library.UpscaledLibraryPath!,
            PathEscaper.EscapeFileName(seriesEntity.PrimaryTitle!));

        if (!Directory.Exists(upscaleTargetFolder))
            fileSystem.CreateDirectory(upscaleTargetFolder);

        var upscaleTargetPath = Path.Combine(upscaleTargetFolder,
            PathEscaper.EscapeFileName(nonUpscaledChapter.FileName));

        // if the file already exists, we don't need to do anything
        if (File.Exists(upscaleTargetPath))
        {
            logger.LogWarning(
                "Upscaled chapter {FoundFileName} already exists in the target path {TargetPath}. Skipping.",
                originalUpscaled.FileName, upscaleTargetPath);
            return null;
        }

        try
        {
            fileSystem.Move(cbzPath, upscaleTargetPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error moving upscaled chapter {FoundFileName} to target path {TargetPath}.",
                originalUpscaled.FileName, upscaleTargetPath);
            return null;
        }

        nonUpscaledChapter.IsUpscaled = true;
        if (profileToUse != null)
        {
            nonUpscaledChapter.UpscalerProfileId = profileToUse.Id;
            nonUpscaledChapter.UpscalerProfile = profileToUse;
        }

        return nonUpscaledChapter;
    }

    [GeneratedRegex(@"(?:^|[\\/])_upscaled(?=[\\/])")]
    private static partial Regex IsUpscaledChapter();

    private record ProcessedChapterInfo(
        FoundChapter Original,
        FoundChapter Renamed,
        bool IsUpscaled,
        UpscalerProfileJsonDto? UpscalerProfile);
}