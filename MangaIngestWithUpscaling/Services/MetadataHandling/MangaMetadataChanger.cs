﻿using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using System.Xml;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

[RegisterScoped]
public class MangaMetadataChanger(
    IMetadataHandlingService metadataHandling,
    ApplicationDbContext dbContext,
    ILogger<MangaMetadataChanger> logger,
    ITaskQueue taskQueue,
    IFileSystem fileSystem) : IMangaMetadataChanger
{
    public async Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = true)
    {
        manga.ChangePrimaryTitle(newTitle, addOldToAlternative);

        // load library and chapters if not already loaded
        if (!dbContext.Entry(manga).Reference(m => m.Library).IsLoaded)
        {
            await dbContext.Entry(manga).Reference(m => m.Library).LoadAsync();
        }
        if (!dbContext.Entry(manga).Collection(m => m.Chapters).IsLoaded)
        {
            await dbContext.Entry(manga).Collection(m => m.Chapters).LoadAsync();
        }

        List<RenameUpscaledChaptersSeriesTask> renameTasks = [];

        var upscaleTasks = await dbContext.PersistedTasks
            .ToAsyncEnumerable()
            .Where(t => t.Data is UpscaleTask)
            .Select(t => (UpscaleTask)t.Data)
            .ToListAsync();

        foreach (var chapter in manga.Chapters)
        {
            try
            {
                var origChapterPath = Path.Combine(manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
                if (!File.Exists(origChapterPath))
                {
                    logger.LogWarning("Chapter file not found: {ChapterPath}", origChapterPath);
                    continue;
                }
                var oldRelativePath = chapter.RelativePath;
                UpdateChapterTitle(newTitle, origChapterPath);
                RelocateChapterToNewTitleDirectory(chapter, origChapterPath, manga.Library.NotUpscaledLibraryPath, manga.PrimaryTitle);

                if (chapter.IsUpscaled || upscaleTasks.Any(t => t.ChapterId == chapter.Id))
                {
                    if (manga.Library.UpscaledLibraryPath == null)
                    {
                        logger.LogWarning("Upscaled library path not set for library {LibraryId}", manga.LibraryId);
                        continue;
                    }
                    var upscaledChapterPath = Path.Combine(manga.Library.UpscaledLibraryPath, oldRelativePath);
                    if (!File.Exists(upscaledChapterPath))
                    {
                        logger.LogWarning("Upscaled chapter file not found: {ChapterPath}", upscaledChapterPath);
                        continue;
                    }
                    renameTasks.Add(
                        new RenameUpscaledChaptersSeriesTask(chapter.Id, upscaledChapterPath, newTitle));
                }
            }
            catch (XmlException ex)
            {
                logger.LogWarning(ex, "Error parsing ComicInfo XML for chapter {ChapterId} ({ChapterPath})", chapter.Id, chapter.RelativePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating metadata for chapter {ChapterId} ({ChapterPath})", chapter.Id, chapter.RelativePath);
            }
        }

        await dbContext.SaveChangesAsync();

        foreach (var task in renameTasks)
        {
            await taskQueue.EnqueueAsync(task);
        }
    }

    private void RelocateChapterToNewTitleDirectory(Chapter chapter, string origChapterPath, string libraryBasePath, string newTitle)
    {
        // move chapter to the correct directory with the new title
        var newChapterPath = Path.Combine(
            libraryBasePath, 
            PathEscaper.EscapeFileName(newTitle), 
            PathEscaper.EscapeFileName(chapter.FileName));
        var newRelativePath = Path.GetRelativePath(libraryBasePath, newChapterPath);
        if (File.Exists(newChapterPath))
        {
            logger.LogWarning("Chapter file already exists: {ChapterPath}", newChapterPath);
            return;
        }
        fileSystem.CreateDirectory(Path.GetDirectoryName(newChapterPath)!);
        fileSystem.Move(origChapterPath, newChapterPath);
        if (!Directory.EnumerateFiles(Path.GetDirectoryName(origChapterPath)!).Any())
        {
            Directory.Delete(Path.GetDirectoryName(origChapterPath)!);
        }
        chapter.RelativePath = newRelativePath;
        dbContext.Update(chapter);
    }

    private void UpdateChapterTitle(string newTitle, string origChapterPath)
    {
        if (!File.Exists(origChapterPath))
        {
            logger.LogWarning("Chapter file not found: {ChapterPath}", origChapterPath);
            return;
        }
        var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(origChapterPath);
        var newMetadata = metadata with { Series = newTitle };
        metadataHandling.WriteComicInfo(origChapterPath, newMetadata);
    }
}
