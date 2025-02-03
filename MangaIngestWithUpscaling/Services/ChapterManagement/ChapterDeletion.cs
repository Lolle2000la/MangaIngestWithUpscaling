using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

public class ChapterDeletion(ApplicationDbContext dbContext) : IChapterDeletion
{
    /// <inheritdoc/>
    public void DeleteChapter(Chapter chapter, bool deleteNormal, bool deleteUpscaled)
    {
        var normalPath = Path.Combine(chapter.Manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
        if (deleteNormal && File.Exists(normalPath))
        {
            File.Delete(normalPath);
            var normalDir = Path.GetDirectoryName(normalPath);
            if (normalDir != null && Directory.EnumerateFiles(normalDir).Count() == 0)
            {
                Directory.Delete(normalDir);
            }
        }
        if (deleteUpscaled && chapter.IsUpscaled && !string.IsNullOrEmpty(chapter.Manga.Library.UpscaledLibraryPath))
        {
            var upscaledPath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, chapter.RelativePath);
            if (File.Exists(upscaledPath))
            {
                File.Delete(upscaledPath);
            }
            var upscaledDir = Path.GetDirectoryName(upscaledPath);
            if (upscaledDir != null && Directory.EnumerateFiles(upscaledDir).Count() == 0)
            {
                Directory.Delete(upscaledDir);
            }
        }

        dbContext.Chapters.Remove(chapter);
    }

    /// <inheritdoc/>
    public void DeleteManga(Manga manga, bool deleteNormal, bool deleteUpscaled)
    {
        foreach (var chapter in manga.Chapters)
        {
            DeleteChapter(chapter, deleteNormal, deleteUpscaled);
        }

        dbContext.MangaSeries.Remove(manga);
    }
}
