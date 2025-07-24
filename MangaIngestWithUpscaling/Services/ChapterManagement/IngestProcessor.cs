using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
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
    IChapterPartMerger chapterPartMerger
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

        var foundChapters = chapterRecognitionService.FindAllChaptersAt(
            library.IngestPath, library.FilterRules, cancellationToken);

        // preserve original series for alternative title
        Dictionary<string, string> originalSeriesMap = await foundChapters.ToDictionaryAsync(c => c.RelativePath,
            c => c.Metadata.Series,
            cancellationToken);

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
            bool shouldMergeChapterParts = library.MergeChapterParts &&
                                           (seriesEntity.MergeChapterParts ?? true);

            List<ProcessedChapterInfo> finalProcessedItems = processedItems;

            // Apply chapter part merging if enabled
            if (shouldMergeChapterParts)
            {
                finalProcessedItems =
                    await ApplyChapterPartMerging(processedItems, library, seriesEntity, cancellationToken);
            }

            // Move chapters to the target path in file system as specified by the libraries NotUpscaledLibraryPath property.
            // Then create a Chapter entity for each chapter and add it to the series.
            foreach (ProcessedChapterInfo pci in finalProcessedItems)
            {
                var originalChapter = pci.Original;
                var renamedChapter = pci.Renamed;

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
                        metadataHandling.WriteComicInfo(convertedChapterPath, desiredMeta);
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
                                metadataHandling.WriteComicInfo(targetPath, cleanMetadata);
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
            await CheckAndMergeRetroactiveChapterParts(seriesEntity, library, cancellationToken);
        }

        logger.LogInformation("Scanned {seriesCount} series in library {libraryName}. Cleaning.",
            chaptersBySeries.Count, library.Name);
        // Clean the ingest path of all empty directories recursively
        FileSystemHelpers.DeleteEmptySubfolders(library.IngestPath, logger);
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

        if (nonUpscaledChapter == null && !string.IsNullOrEmpty(renamedUpscaled.Metadata.ChapterTitle))
        {
            nonUpscaledChapter = seriesEntity.Chapters.FirstOrDefault(c =>
            {
                try
                {
                    // Path to the existing non-upscaled chapter file in the library
                    var existingNonUpscaledFilePath = Path.Combine(library.NotUpscaledLibraryPath, c.RelativePath);
                    if (!File.Exists(existingNonUpscaledFilePath))
                    {
                        return false;
                    }

                    var existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(existingNonUpscaledFilePath);
                    return existingMetadata.ChapterTitle == renamedUpscaled.Metadata.ChapterTitle;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error reading metadata for existing chapter {chapterPath} ({chapterId}) during upscaled matching.",
                        Path.Combine(library.NotUpscaledLibraryPath, c.RelativePath), c.Id);
                    return false;
                }
            });
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
                metadataHandling.WriteComicInfo(cbzPath, finalDesiredMetadata);
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

    private async Task<List<ProcessedChapterInfo>> ApplyChapterPartMerging(
        List<ProcessedChapterInfo> processedItems,
        Library library,
        Manga seriesEntity,
        CancellationToken cancellationToken)
    {
        try
        {
            // Separate upscaled and non-upscaled chapters
            List<FoundChapter> nonUpscaledChapters =
                processedItems.Where(p => !p.IsUpscaled).Select(p => p.Renamed).ToList();
            List<ProcessedChapterInfo> upscaledChapters = processedItems.Where(p => p.IsUpscaled).ToList();

            if (!nonUpscaledChapters.Any())
            {
                return processedItems; // No chapters to merge
            }

            // Load existing chapters to determine latest chapter numbers
            if (!dbContext.Entry(seriesEntity).Collection(s => s.Chapters).IsLoaded)
            {
                await dbContext.Entry(seriesEntity).Collection(s => s.Chapters).LoadAsync(cancellationToken);
            }

            // Find the latest chapter numbers from existing chapters and new chapters
            HashSet<string> existingChapterNumbers = seriesEntity.Chapters
                .Select(c => ExtractChapterNumber(c.FileName))
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet();

            HashSet<string> newChapterNumbers = nonUpscaledChapters
                .Select(c => ExtractChapterNumber(c.FileName))
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet();

            HashSet<string> allChapterNumbers = existingChapterNumbers.Union(newChapterNumbers).ToHashSet();

            // Identify chapter parts that should be merged from the newly processed chapters
            Dictionary<string, List<FoundChapter>> chaptersToMerge = chapterPartMerger.GroupChapterPartsForMerging(
                nonUpscaledChapters,
                baseNumber => IsLatestChapter(baseNumber, allChapterNumbers));

            if (!chaptersToMerge.Any())
            {
                return processedItems; // No chapter parts to merge
            }

            var mergedProcessedItems = new List<ProcessedChapterInfo>();
            var processedChapterPaths = new HashSet<string>();

            // Process chapter merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Merging {PartCount} chapter parts for base number {BaseNumber} in series {SeriesTitle}",
                        chapterParts.Count, baseNumber, seriesEntity.PrimaryTitle);

                    // Merge the chapters
                    var (mergedChapter, originalParts) = await chapterPartMerger.MergeChapterPartsAsync(
                        chapterParts,
                        library.IngestPath,
                        library.IngestPath,
                        baseNumber,
                        cancellationToken);

                    // Find the original ProcessedChapterInfo for metadata
                    ProcessedChapterInfo? originalProcessedItem = processedItems.FirstOrDefault(p =>
                        chapterParts.Any(cp => cp.RelativePath == p.Renamed.RelativePath));

                    if (originalProcessedItem != null)
                    {
                        // Create new ProcessedChapterInfo for the merged chapter
                        var mergedProcessedItem = new ProcessedChapterInfo(
                            mergedChapter, // Use merged chapter as both original and renamed
                            mergedChapter,
                            false, // Merged chapters are not upscaled initially
                            null);

                        mergedProcessedItems.Add(mergedProcessedItem);

                        // Store merge information for potential reversion
                        // This will be saved to the database after the chapter entity is created
                        var mergeInfo = new MergedChapterInfo
                        {
                            OriginalParts = originalParts,
                            MergedChapterNumber = baseNumber,
                            CreatedAt = DateTime.UtcNow
                        };

                        // We'll need to associate this with the chapter entity later
                        // For now, store it in a way we can access it later
                        mergedChapter = mergedChapter with
                        {
                            Metadata = mergedChapter.Metadata with
                            {
                                ChapterTitle = mergedChapter.Metadata.ChapterTitle +
                                               $"__MERGE_INFO__{JsonSerializer.Serialize(mergeInfo)}"
                            }
                        };
                        mergedProcessedItems[^1] = mergedProcessedItem with { Renamed = mergedChapter };
                    }

                    // Mark these chapter parts as processed
                    foreach (FoundChapter part in chapterParts)
                    {
                        processedChapterPaths.Add(part.RelativePath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to merge chapter parts for base number {BaseNumber} in series {SeriesTitle}",
                        baseNumber, seriesEntity.PrimaryTitle);

                    // If merging fails, add the original chapters individually
                    foreach (FoundChapter part in chapterParts)
                    {
                        ProcessedChapterInfo? originalProcessedItem =
                            processedItems.FirstOrDefault(p => p.Renamed.RelativePath == part.RelativePath);
                        if (originalProcessedItem != null)
                        {
                            mergedProcessedItems.Add(originalProcessedItem);
                        }

                        processedChapterPaths.Add(part.RelativePath);
                    }
                }
            }

            // Add non-merged chapters to the result
            foreach (ProcessedChapterInfo processedItem in processedItems)
            {
                if (!processedChapterPaths.Contains(processedItem.Renamed.RelativePath))
                {
                    mergedProcessedItems.Add(processedItem);
                }
            }

            logger.LogInformation(
                "Chapter part merging completed. Processed {OriginalCount} chapters, resulted in {FinalCount} chapters",
                processedItems.Count, mergedProcessedItems.Count);

            return mergedProcessedItems;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error during chapter part merging for series {SeriesTitle}. Falling back to original chapters.",
                seriesEntity.PrimaryTitle);
            return processedItems;
        }
    }

    /// <summary>
    ///     Checks for existing chapter parts that can now be merged because they're no longer the latest
    ///     This should be called after new chapters are ingested
    /// </summary>
    private async Task CheckAndMergeRetroactiveChapterParts(
        Manga seriesEntity,
        Library library,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only proceed if merging is enabled
            bool shouldMerge = seriesEntity.MergeChapterParts ?? library.MergeChapterParts;
            if (!shouldMerge)
            {
                return;
            }

            // Get all chapter numbers including existing ones
            HashSet<string> allChapterNumbers = seriesEntity.Chapters
                .Select(c => ExtractChapterNumber(c.FileName))
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet();

            // Get IDs of chapters that are already merged to exclude them
            HashSet<int> mergedChapterIds = await dbContext.MergedChapterInfos
                .Where(m => seriesEntity.Chapters.Any(c => c.Id == m.ChapterId))
                .Select(m => m.ChapterId)
                .ToHashSetAsync(cancellationToken);

            // Get existing chapters that might need merging
            List<FoundChapter> existingChaptersForMerging = seriesEntity.Chapters
                .Where(c => !mergedChapterIds.Contains(c.Id)) // Not already merged
                .Select(c => new FoundChapter(
                    c.FileName,
                    c.RelativePath,
                    ChapterStorageType.Cbz,
                    new ExtractedMetadata(c.FileName, null, null)))
                .ToList();

            if (!existingChaptersForMerging.Any())
            {
                return;
            }

            // Find chapters that can be merged
            Dictionary<string, List<FoundChapter>> chaptersToMerge = chapterPartMerger.GroupChapterPartsForMerging(
                existingChaptersForMerging,
                baseNumber => IsLatestChapter(baseNumber, allChapterNumbers));

            if (!chaptersToMerge.Any())
            {
                return;
            }

            logger.LogInformation(
                "Found {GroupCount} groups of existing chapter parts that can now be merged retroactively for series {SeriesTitle}",
                chaptersToMerge.Count, seriesEntity.PrimaryTitle);

            // Process each group for merging
            foreach (var (baseNumber, chapterParts) in chaptersToMerge)
            {
                try
                {
                    logger.LogInformation(
                        "Retroactively merging {PartCount} existing chapter parts for base number {BaseNumber} in series {SeriesTitle}",
                        chapterParts.Count, baseNumber, seriesEntity.PrimaryTitle);

                    // Merge the chapters
                    var (mergedChapter, originalParts) = await chapterPartMerger.MergeChapterPartsAsync(
                        chapterParts,
                        library.NotUpscaledLibraryPath,
                        library.NotUpscaledLibraryPath,
                        baseNumber,
                        cancellationToken);

                    // Find the existing database chapters to update
                    List<Chapter> dbChaptersToUpdate = seriesEntity.Chapters
                        .Where(c => chapterParts.Any(p => p.RelativePath == c.RelativePath))
                        .ToList();

                    if (dbChaptersToUpdate.Count == chapterParts.Count)
                    {
                        // Keep the first chapter record and update it to represent the merged chapter
                        Chapter primaryChapter = dbChaptersToUpdate.First();
                        primaryChapter.FileName = mergedChapter.FileName;
                        primaryChapter.RelativePath = mergedChapter.RelativePath;

                        // Create merge tracking record
                        var mergedChapterInfo = new MergedChapterInfo
                        {
                            ChapterId = primaryChapter.Id,
                            OriginalParts = originalParts,
                            MergedChapterNumber = baseNumber,
                            CreatedAt = DateTime.UtcNow
                        };

                        dbContext.MergedChapterInfos.Add(mergedChapterInfo);

                        // Remove the other chapter records
                        List<Chapter> chaptersToRemove = dbChaptersToUpdate.Skip(1).ToList();
                        dbContext.Chapters.RemoveRange(chaptersToRemove);

                        await dbContext.SaveChangesAsync(cancellationToken);

                        logger.LogInformation(
                            "Successfully merged {PartCount} existing chapter parts into {MergedFileName} for series {SeriesTitle}",
                            chapterParts.Count, mergedChapter.FileName, seriesEntity.PrimaryTitle);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to retroactively merge existing chapter parts for base number {BaseNumber} in series {SeriesTitle}",
                        baseNumber, seriesEntity.PrimaryTitle);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during retroactive chapter part merging for series {SeriesTitle}",
                seriesEntity.PrimaryTitle);
        }
    }

    private bool IsLatestChapter(string baseNumber, HashSet<string> allChapterNumbers)
    {
        if (!decimal.TryParse(baseNumber, out decimal baseNum))
        {
            return false;
        }

        // Check if there are any chapter numbers higher than this base number
        foreach (string chapterNumber in allChapterNumbers)
        {
            if (decimal.TryParse(chapterNumber, out decimal num))
            {
                decimal chapterBaseNum = Math.Floor(num);
                if (chapterBaseNum > baseNum)
                {
                    return false; // There's a higher chapter, so this isn't the latest
                }
            }
        }

        return true; // No higher chapters found, this is the latest
    }

    private string? ExtractChapterNumber(string fileName)
    {
        // Try to extract chapter number from filename
        Match match = Regex.Match(fileName,
            @"(?:Chapter\s*(?<num>\d+(?:\.\d+)?)|第(?<num>\d+(?:\.\d+)?)(?:話|章)|Kapitel\s*(?<num>\d+(?:\.\d+)?))");

        if (match.Success)
        {
            return match.Groups["num"].Value;
        }

        // Also try a simpler pattern that just looks for numbers
        Match simpleMatch = Regex.Match(fileName, @"(\d+(?:\.\d+)?)");
        if (simpleMatch.Success)
        {
            return simpleMatch.Groups[1].Value;
        }

        return null;
    }

    private record ProcessedChapterInfo(
        FoundChapter Original,
        FoundChapter Renamed,
        bool IsUpscaled,
        UpscalerProfileJsonDto? UpscalerProfile);
}