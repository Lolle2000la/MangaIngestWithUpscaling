using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
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
    IUpscalerJsonHandlingService upscalerJsonHandlingService
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

            if (!isUpscaled)
            {
                string fullPath = Path.Combine(library.IngestPath, originalChapter.RelativePath);
                upscalerProfileDto =
                    await upscalerJsonHandlingService.ReadUpscalerJsonAsync(fullPath, cancellationToken);
                isUpscaled = upscalerProfileDto != null;
            }

            processedChapters.Add(new ProcessedChapterInfo(originalChapter, renamedChapter, isUpscaled,
                upscalerProfileDto));
        }

        // group chapters by new series title
        var chaptersBySeries = processedChapters
            .GroupBy(pci => pci.Renamed.Metadata.Series)
            .ToDictionary(g => g.Key,
                g => g.OrderBy(pci => pci.IsUpscaled).ToList());

        List<(Chapter, UpscalerProfile)> chaptersToUpscale = new();
        List<Task> scans = new();
        var upscalerProfileCache = new Dictionary<UpscalerProfileJsonDto, UpscalerProfile>();

        foreach (var (series, processedItems) in chaptersBySeries)
        {
            var firstOriginalRelativePath = processedItems.First().Original.RelativePath;
            var originalSeriesTitle = originalSeriesMap[firstOriginalRelativePath];
            Manga seriesEntity = await GetMangaSeriesEntity(library, series, originalSeriesTitle, cancellationToken);

            // Move chapters to the target path in file system as specified by the libraries NotUpscaledLibraryPath property.
            // Then create a Chapter entity for each chapter and add it to the series.
            foreach (var pci in processedItems)
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
                        var chapterIndex = chaptersToUpscale.FindIndex(c => c.Item1.Id == foundMatch.Id);
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
                scans.Add(chapterChangedNotifier.Notify(chapterEntity, false));

                if (library.UpscaleOnIngest && seriesEntity.ShouldUpscale != false &&
                    library.UpscalerProfileId.HasValue)
                {
                    dbContext.Entry(chapterEntity).Reference(c => c.UpscalerProfile).Load();
                    chaptersToUpscale.Add((chapterEntity, library.UpscalerProfile!));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var chapterTuple in chaptersToUpscale)
        {
            var upscaleTask =
                new UpscaleTask(chapterTuple.Item1,
                    chapterTuple
                        .Item2); // if I use destructuring here, the value becomes 0. I checked with the debugger and I have no idea why this happens.
            await taskQueue.EnqueueAsync(upscaleTask);
        }

        await Task.WhenAll(scans);

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
                        p.Name == upscalerProfileDto.Name &&
                        p.UpscalerMethod == upscalerProfileDto.UpscalerMethod &&
                        p.ScalingFactor == scaleFactor &&
                        p.CompressionFormat == upscalerProfileDto.CompressionFormat &&
                        p.Quality == upscalerProfileDto.Quality &&
                        !p.Deleted,
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