using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.MetadataHandling;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public class MangaMetadataChanger(
    IMetadataHandlingService metadataHandling,
    ApplicationDbContext dbContext,
    ILogger<MangaMetadataChanger> logger) : IMangaMetadataChanger
{
    public async Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = false)
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
                UpdateChapterTitleMetadata(newTitle, origChapterPath);

                if (chapter.IsUpscaled)
                {
                    if (manga.Library.UpscaledLibraryPath == null)
                    {
                        logger.LogWarning("Upscaled library path not set for library {LibraryId}", manga.LibraryId);
                        continue;
                    }
                    var upscaledChapterPath = Path.Combine(manga.Library.UpscaledLibraryPath, chapter.RelativePath);
                    UpdateChapterTitleMetadata(newTitle, upscaledChapterPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating metadata for chapter {ChapterId} ({ChapterPath})", chapter.Id, chapter.RelativePath);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private void UpdateChapterTitleMetadata(string newTitle, string origChapterPath)
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
