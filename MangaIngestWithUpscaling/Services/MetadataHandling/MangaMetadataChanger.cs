using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using System.Xml;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public class MangaMetadataChanger(
    IMetadataHandlingService metadataHandling,
    ApplicationDbContext dbContext,
    ILogger<MangaMetadataChanger> logger) : IMangaMetadataChanger
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

        foreach (var chapter in manga.Chapters)
        {
            try
            {
                var origChapterPath = Path.Combine(manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
                UpdateChapterTitle(newTitle, origChapterPath);
                RelocateChapterToNewTitleDirectory(chapter, origChapterPath, manga.Library.NotUpscaledLibraryPath, manga.PrimaryTitle);

                if (chapter.IsUpscaled)
                {
                    if (manga.Library.UpscaledLibraryPath == null)
                    {
                        logger.LogWarning("Upscaled library path not set for library {LibraryId}", manga.LibraryId);
                        continue;
                    }
                    var upscaledChapterPath = Path.Combine(manga.Library.UpscaledLibraryPath, chapter.RelativePath);
                    UpdateChapterTitle(newTitle, upscaledChapterPath);
                    RelocateChapterToNewTitleDirectory(chapter, upscaledChapterPath, manga.Library.UpscaledLibraryPath, manga.PrimaryTitle);
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
    }

    private void RelocateChapterToNewTitleDirectory(Chapter chapter, string origChapterPath, string libraryBasePath, string newTitle)
    {
        // move chapter to the correct directory with the new title
        var newChapterPath = Path.Combine(libraryBasePath, newTitle, chapter.FileName);
        var newRelativePath = Path.GetRelativePath(libraryBasePath, newChapterPath);
        Directory.CreateDirectory(Path.GetDirectoryName(newChapterPath));
        File.Move(origChapterPath, newChapterPath);
        if (!Directory.EnumerateFiles(Path.GetDirectoryName(origChapterPath)).Any())
        {
            Directory.Delete(Path.GetDirectoryName(origChapterPath));
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
