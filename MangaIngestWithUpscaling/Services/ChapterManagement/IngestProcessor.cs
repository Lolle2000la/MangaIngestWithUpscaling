using DynamicData;
using MangaIngestWithUpscaling.Components.FileSystem;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.CbzConversion;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

[RegisterScoped]
public partial class IngestProcessor(ApplicationDbContext dbContext,
                IChapterInIngestRecognitionService chapterRecognitionService,
                ILibraryRenamingService renamingService, // add renaming service
                ICbzConverter cbzConverter,
                ILogger<IngestProcessor> logger,
                ITaskQueue taskQueue,
                IMetadataHandlingService metadataHandling,
                IFileSystem fileSystem,
                IChapterChangedNotifier chapterChangedNotifier
                ) : IIngestProcessor
{
    private record ProcessedChapterInfo(FoundChapter Original, FoundChapter Renamed);

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
            library.IngestPath, library.FilterRules);

        // preserve original series for alternative title
        var originalSeriesMap = foundChapters.ToDictionary(c => c.RelativePath, c => c.Metadata.Series);

        // apply rename rules and keep track of original and renamed versions
        var processedChapters = foundChapters
            .Select(originalChapter => new ProcessedChapterInfo(
                Original: originalChapter,
                Renamed: renamingService.ApplyRenameRules(originalChapter, library.RenameRules)))
            .ToList();

        // group chapters by new series title
        var chaptersBySeries = processedChapters
            .GroupBy(pci => pci.Renamed.Metadata.Series)
            .ToDictionary(g => g.Key,
                g => g.OrderBy(pci => IsUpscaledChapter().IsMatch(pci.Renamed.RelativePath)).ToList());

        List<(Chapter, UpscalerProfile)> chaptersToUpscale = new();
        List<Task> scans = new();

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
                if (IsUpscaledChapter().IsMatch(renamedChapter.RelativePath))
                {
                    if (!renamedChapter.RelativePath.EndsWith(".cbz"))
                    {
                        logger.LogError("Upscaled chapter {chapter} is not a cbz file. At this moment only cbz-files are supported for ingest of already existing files.", renamedChapter.RelativePath);
                        continue;
                    }

                    var foundMatch = IngestUpscaledChapterIfMatchFound(renamedChapter, seriesEntity, library);
                    if (foundMatch != null)
                    {
                        var chapterIndex = chaptersToUpscale.FindIndex(c => c.Item1.Id == foundMatch.Id);
                        if (chapterIndex != -1)
                            chaptersToUpscale.RemoveAt(chapterIndex);
                    }
                    continue;
                }

                FoundChapter chapterCbzFromConverter;
                string sourceCbzAcutalPathInIngest;
                try
                {
                    // ConvertToCbz should use originalChapter to find source files.
                    // The FileName in the returned FoundChapter will be based on originalChapter.FileName.
                    chapterCbzFromConverter = cbzConverter.ConvertToCbz(originalChapter, library.IngestPath);
                    
                    // Path to the newly created CBZ (e.g., originalName.cbz)
                    sourceCbzAcutalPathInIngest = Path.Combine(library.IngestPath, chapterCbzFromConverter.RelativePath);
                    
                    // change title in metadata to the primary title of the series
                    var existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(sourceCbzAcutalPathInIngest);
                    metadataHandling.WriteComicInfo(sourceCbzAcutalPathInIngest, existingMetadata with { Series = seriesEntity.PrimaryTitle });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error converting chapter {chapter} to cbz.", originalChapter.RelativePath);
                    continue;
                }
                
                // Target path uses renamedChapter.FileName
                var targetPath = Path.Combine(
                    library.NotUpscaledLibraryPath,
                    PathEscaper.EscapeFileName(seriesEntity.PrimaryTitle!),
                    PathEscaper.EscapeFileName(renamedChapter.FileName)); // Use renamedChapter.FileName
                
                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                         renamedChapter.FileName, targetPath); // Log renamed name
                    // Optionally, clean up the created CBZ from ingest path if it's not moved
                    // fileSystem.DeleteFile(sourceCbzAcutalPathInIngest); 
                    continue;
                }
                if (!Directory.Exists(Path.GetDirectoryName(targetPath)))
                    fileSystem.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                
                string relativePathForDb = Path.GetRelativePath(library.NotUpscaledLibraryPath, targetPath);
                if (await dbContext.Chapters.AnyAsync(c => c.RelativePath == relativePathForDb && c.Manga.Id == seriesEntity.Id, cancellationToken: cancellationToken))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the database. Skipping.", renamedChapter.FileName);
                    // fileSystem.DeleteFile(sourceCbzAcutalPathInIngest);
                    continue;
                }
                // This check is redundant due to the one above, but kept for safety, ensure it uses targetPath.
                // if (File.Exists(targetPath)) 
                // {
                //     logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                //         renamedChapter.FileName, targetPath);
                //     continue;
                // }

                // Move the created/processed CBZ from its location in ingestPath to the final targetPath
                fileSystem.Move(sourceCbzAcutalPathInIngest, targetPath);
                var chapterEntity = new Chapter
                {
                    FileName = renamedChapter.FileName, // Use renamed name
                    Manga = seriesEntity,
                    MangaId = seriesEntity.Id,
                    RelativePath = relativePathForDb, // Use relative path based on target
                    IsUpscaled = false
                };
                seriesEntity.Chapters.Add(chapterEntity);
                scans.Add(chapterChangedNotifier.Notify(chapterEntity, false));

                if (library.UpscaleOnIngest && seriesEntity.ShouldUpscale != false && library.UpscalerProfileId.HasValue)
                {
                    dbContext.Entry(chapterEntity).Reference(c => c.UpscalerProfile).Load();
                    chaptersToUpscale.Add((chapterEntity!, library.UpscalerProfile));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var chapterTuple in chaptersToUpscale)
        {
            var upscaleTask = new UpscaleTask(chapterTuple.Item1, chapterTuple.Item2);  // if I use destructuring here, the value becomes 0. I checked with the debugger and I have no idea why this happens.
            await taskQueue.EnqueueAsync(upscaleTask);
        }

        await Task.WhenAll(scans);

        logger.LogInformation("Scanned {seriesCount} series in library {libraryName}. Cleaning.", chaptersBySeries.Count, library.Name);
        // Clean the ingest path of all empty directories recursively
        FileSystemHelpers.DeleteEmptySubfolders(library.IngestPath, logger);
    }

    private async Task<Manga> GetMangaSeriesEntity(Library library, string newSeries, string originalSeries, CancellationToken cancellationToken)
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
                seriesEntity.OtherTitles.Add(new MangaAlternativeTitle { Manga = seriesEntity, Title = originalSeries });
            }
            dbContext.MangaSeries.Add(seriesEntity);
        }

        if (seriesEntity.Chapters == null)
        {
            seriesEntity.Chapters = new List<Chapter>();
        }

        return seriesEntity;
    }

    private Chapter? IngestUpscaledChapterIfMatchFound(FoundChapter found, Manga seriesEntity, Library library)
    {
        string? OriginalChapterName(Chapter existing)
        {
            try
            {

                var originalPath = Path.Combine(library.IngestPath, existing.RelativePath);
                var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(originalPath);
                return metadata.ChapterTitle;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reading metadata for chapter {chapter} ({chapterId}). This suggests a possibly corrupt archive.", existing.RelativePath, existing.Id);
                return null;
            }
        }

        // find the non-upscaled chapter that matches the found upscaled chapter
        var nonUpscaledChapter = seriesEntity.Chapters.FirstOrDefault(c =>
                (c.FileName == found.FileName || c.FileName == PathEscaper.EscapeFileName(found.FileName))); // try comparing escaped filenames

        // try comparing the chapter title in the metadata if no match was found by filename
        nonUpscaledChapter ??= seriesEntity.Chapters.FirstOrDefault(c =>
                !string.IsNullOrEmpty(found.Metadata.ChapterTitle) && OriginalChapterName(c) == found.Metadata.ChapterTitle);

        if (nonUpscaledChapter == null)
        {
            logger.LogWarning("Upscaled chapter {FoundFileName} does not have a matching non-upscaled chapter in series {MangaTitle} ({MangaId}). Skipping.", found.FileName, seriesEntity.PrimaryTitle, seriesEntity.Id);
            return null;
        }
        if (library.UpscaledLibraryPath == null)
        {
            logger.LogWarning("Found matching upscaled chapter, but no path is set for the upscale-folder in library {LibraryName}.\n\n" +
                "Found: {RelativePath}", library.Name, found.RelativePath);
        }

        // apply primary title to metadata
        var cbzPath = Path.Combine(library.IngestPath, found.RelativePath);
        try
        {
            var existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(cbzPath);
            metadataHandling.WriteComicInfo(cbzPath, existingMetadata with { Series = seriesEntity.PrimaryTitle });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating metadata for chapter {chapter}. This suggests a possibly corrupt archive.", found.RelativePath);
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
            logger.LogWarning("Upscaled chapter {FoundFileName} already exists in the target path {TargetPath}. Skipping.", found.FileName, upscaleTargetPath);
            return null;
        }

        try
        {
            fileSystem.Move(cbzPath, upscaleTargetPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error moving upscaled chapter {FoundFileName} to target path {TargetPath}.", found.FileName, upscaleTargetPath);
            return null;
        }

        nonUpscaledChapter.IsUpscaled = true;
        dbContext.Update(nonUpscaledChapter);

        return nonUpscaledChapter;
    }

    [GeneratedRegex(@"(?:^|[\\/])_upscaled(?=[\\/])")]
    private static partial Regex IsUpscaledChapter();
}
