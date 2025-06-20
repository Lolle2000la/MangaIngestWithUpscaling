using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Services.Integrations;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using System.Xml;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

[RegisterScoped]
public class MangaMetadataChanger(
    IMetadataHandlingService metadataHandling,
    ApplicationDbContext dbContext,
    IDialogService dialogService,
    ILogger<MangaMetadataChanger> logger,
    ITaskQueue taskQueue,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : IMangaMetadataChanger
{
    /// <inheritdoc/>
    public void ApplyMangaTitleToUpscaled(Chapter chapter, string newTitle, string origChapterPath)
    {
        if (!File.Exists(origChapterPath))
        {
            throw new InvalidOperationException("Chapter file not found.");
        }

        if (chapter.Manga == null || chapter.Manga.Library == null)
        {
            throw new ArgumentNullException(
                "Chapter manga or library not found. Please ensure you have loaded it with the chapter.");
        }

        if (chapter.Manga.Library.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException("Upscaled library path not set.");
        }

        UpdateChapterTitle(newTitle, origChapterPath);
        RelocateChapterToNewTitleDirectory(chapter, origChapterPath, chapter.Manga.Library.UpscaledLibraryPath,
            newTitle);
        _ = chapterChangedNotifier.Notify(chapter, true);
    }

    /// <inheritdoc/>
    public async Task<RenameResult> ChangeMangaTitle(Manga manga, string newTitle, bool addOldToAlternative = true,
        CancellationToken cancellationToken = default)
    {
        var possibleCurrent = await dbContext.MangaSeries.FirstOrDefaultAsync(m =>
                m.Id != manga.Id && // prevent accidental self-merge
            (m.PrimaryTitle == newTitle || m.OtherTitles.Any(t => t.Title == newTitle)),
            cancellationToken: cancellationToken);

        if (possibleCurrent != null)
        {
            bool? consentToMerge = await dialogService.ShowMessageBox("Merge into existing manga of same name?",
                "The title you are trying to rename to already has an existing entry. " +
                "Do you want to merge this manga into the existing one?",
                yesText: "Merge", cancelText: "Cancel");
            if (consentToMerge == true)
            {
                await taskQueue.EnqueueAsync(new MergeMangaTask(possibleCurrent, [manga]));
                return RenameResult.Merged;
            }

            return RenameResult.Cancelled;
        }

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
                RelocateChapterToNewTitleDirectory(chapter, origChapterPath, manga.Library.NotUpscaledLibraryPath,
                    manga.PrimaryTitle);
                _ = chapterChangedNotifier.Notify(chapter, false);

                if (chapter.IsUpscaled)
                {
                    ApplyMangaTitleToUpscaled(chapter, newTitle,
                        Path.Combine(manga.Library.UpscaledLibraryPath!, oldRelativePath));
                }
            }
            catch (XmlException ex)
            {
                logger.LogWarning(ex, "Error parsing ComicInfo XML for chapter {ChapterId} ({ChapterPath})", chapter.Id,
                    chapter.RelativePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating metadata for chapter {ChapterId} ({ChapterPath})", chapter.Id,
                    chapter.RelativePath);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return RenameResult.Ok;
    }

    private void RelocateChapterToNewTitleDirectory(Chapter chapter, string origChapterPath, string libraryBasePath,
        string newTitle)
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
        FileSystemHelpers.DeleteIfEmpty(Path.GetDirectoryName(origChapterPath)!, logger);
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

    /// <inheritdoc/>
    public async Task ChangeChapterTitle(Chapter chapter, string newTitle)
    {
        await dbContext.Entry(chapter).Reference(c => c.Manga).LoadAsync();
        await dbContext.Entry(chapter.Manga).Reference(m => m.Library).LoadAsync();

        try
        {
            metadataHandling.WriteComicInfo(chapter.NotUpscaledFullPath,
                metadataHandling.GetSeriesAndTitleFromComicInfo(chapter.NotUpscaledFullPath) with
                {
                    ChapterTitle = newTitle
                });

            if (chapter.IsUpscaled)
            {
                if (chapter.UpscaledFullPath == null)
                {
                    logger.LogWarning("Upscaled chapter file not found: {ChapterPath}", chapter.UpscaledFullPath);
                    return;
                }

                metadataHandling.WriteComicInfo(chapter.UpscaledFullPath,
                    metadataHandling.GetSeriesAndTitleFromComicInfo(chapter.UpscaledFullPath) with
                    {
                        ChapterTitle = newTitle
                    });
            }
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "Error parsing ComicInfo XML for chapter {ChapterId} ({ChapterPath})", chapter.Id,
                chapter.RelativePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating metadata for chapter {ChapterId} ({ChapterPath})", chapter.Id,
                chapter.RelativePath);
        }
    }
}