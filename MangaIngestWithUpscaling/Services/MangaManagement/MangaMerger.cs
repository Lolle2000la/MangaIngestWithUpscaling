﻿using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

[RegisterScoped]
public class MangaMerger(
    ApplicationDbContext dbContext,
    IMetadataHandlingService metadataHandling,
    IMangaMetadataChanger metadataChanger,
    ILogger<MangaMerger> logger,
    ITaskQueue taskQueue,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : IMangaMerger
{
    /// <inheritdoc/>
    public async Task MergeAsync(Manga primary, IEnumerable<Manga> mergedInto, CancellationToken cancellationToken = default)
    {
        if (!dbContext.Entry(primary).Reference(m => m.Library).IsLoaded)
            await dbContext.Entry(primary).Reference(m => m.Library).LoadAsync(cancellationToken);

        foreach (var manga in mergedInto)
        {
            if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
                await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync(cancellationToken);
            if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
                await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync(cancellationToken);
            if (!dbContext.Entry(manga).Collection(m => m.OtherTitles).IsLoaded)
                await dbContext.Entry(manga).Collection(m => m.OtherTitles).LoadAsync(cancellationToken);

            // collect all the chapters to move in a separate list to avoid modifying the collection while iterating
            List<Chapter> chaptersToMove = [];

            foreach (var chapter in manga.Chapters)
            {
                // change title in metadata to the primary title of the series
                var chapterPath = Path.Combine(manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
                if (!File.Exists(chapterPath))
                {
                    logger.LogWarning("Chapter {fileName} does not exist in {LibraryId} for {MangaId}. Therefore skipping.",
                        chapter.FileName, manga.LibraryId, manga.Id);
                    continue;
                }

                ExtractedMetadata? existingMetadata = null;

                try
                {
                    existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapterPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read metadata from {chapterPath}. Skipping.",
                        chapterPath);
                    continue;
                }
                metadataHandling.WriteComicInfo(chapterPath, existingMetadata with { Series = primary.PrimaryTitle });

                // move chapter into the primary mangas library and folder
                var targetPath = Path.Combine(
                    primary.Library.NotUpscaledLibraryPath,
                    PathEscaper.EscapeFileName(primary.PrimaryTitle!),
                    PathEscaper.EscapeFileName(chapter.FileName));
                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                        chapter.FileName, targetPath);
                    continue;
                }
                try
                {
                    fileSystem.Move(chapterPath, targetPath);
                    _ = chapterChangedNotifier.Notify(chapter, false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to move chapter {fileName} from {chapterPath} to {targetPath}.",
                        chapter.FileName, chapterPath, targetPath);
                    continue;
                }
                if (chapter.IsUpscaled && !string.IsNullOrEmpty(manga.Library.UpscaledLibraryPath))
                {
                    var upscaledPath = Path.Combine(manga.Library.UpscaledLibraryPath, chapter.RelativePath);
                    if (File.Exists(upscaledPath))
                    {
                        if (primary.Library.UpscaledLibraryPath != null)
                        {
                            try
                            {
                                metadataChanger.ApplyMangaTitleToUpscaled(chapter, primary.PrimaryTitle!, upscaledPath);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed to update metadata of upscaled chapter {fileName} in {MangaId}.",
                                    chapter.FileName, manga.Id);
                            }
                        }
                        else
                        {
                            logger.LogWarning("Chapter {fileName} in {MangaId} is upscaled but the primary manga does not have an upscaled library path. Skipping.",
                                chapter.FileName, manga.Id);
                        }
                    }
                }
                chapter.RelativePath = Path.GetRelativePath(primary.Library.NotUpscaledLibraryPath, targetPath);
                chapter.Manga = primary;
                chapter.MangaId = primary.Id;
                chaptersToMove.Add(chapter);
            }

            // now actually move the chapters.
            foreach (var chapter in chaptersToMove)
            {
                primary.Chapters.Add(chapter);
                manga.Chapters.Remove(chapter);
            }

            // This will remove the manga from the library if it has no chapters left
            // If it doesn't then that means that some chapters failed to move and the manga should not be removed
            // This is an case that should be handled by the user with no reasonable way to automatically judge
            // what to do with the manga.
            if (manga.Chapters.Count == 0)
            {
                dbContext.MangaSeries.Remove(manga);

                // remove the folder if it is empty
                var notUpscaledMangaDir = Path.Combine(manga.Library.NotUpscaledLibraryPath, manga.PrimaryTitle);
                if (!FileSystemHelpers.DeleteIfEmpty(notUpscaledMangaDir, logger))
                {
                    logger.LogWarning("Manga {MangaId} was removed but the folder {notUpscaledMangaDir} was not empty.",
                        manga.Id, notUpscaledMangaDir);
                }
                var upscaledMangaDir = Path.Combine(manga.Library.UpscaledLibraryPath, manga.PrimaryTitle);
                FileSystemHelpers.DeleteIfEmpty(upscaledMangaDir, logger);

                try
                {
                    // add the other titles to the primary manga if they are not already there
                    if (!primary.OtherTitles.Any(t => t.Title == manga.PrimaryTitle))
                    {
                        primary.OtherTitles.Add(new MangaAlternativeTitle
                        {
                            Title = manga.PrimaryTitle,
                            Manga = primary,
                            MangaId = primary.Id
                        });
                    }
                    foreach (var title in manga.OtherTitles
                        .Where(title => !primary.OtherTitles.Any(t => t.Title == title.Title))
                        .ToList())
                    {
                        title.Manga = primary;
                        title.MangaId = primary.Id;
                        primary.OtherTitles.Add(title);
                        manga.OtherTitles.Remove(title);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to move other titles from {MangaId} to {PrimaryMangaId}.",
                        manga.Id, primary.Id);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            _ = Task.Run(() =>
            {
                foreach (var uniqueLibraryPath in mergedInto
                    .SelectMany(m => new[] { m.Library.NotUpscaledLibraryPath, m.Library.UpscaledLibraryPath })
                    .Where(path => path is not null)
                    .Distinct())
                {
                    // remove the folder if it is empty
                    FileSystemHelpers.DeleteEmptySubfolders(uniqueLibraryPath!, logger);
                }
            });
        }
    }
}
