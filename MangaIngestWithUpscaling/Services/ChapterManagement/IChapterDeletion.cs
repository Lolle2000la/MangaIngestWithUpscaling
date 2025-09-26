using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

public interface IChapterDeletion
{
    /// <summary>
    /// Deletes a chapter from the library.
    /// </summary>
    /// <param name="context">The database context to use.</param>
    /// <param name="chapter">The chapter to delete.</param>
    /// <param name="deleteNormal">Whether to delete the normal version of the chapter.</param>
    /// <param name="deleteUpscaled">Whether to delete the upscaled version of the chapter.</param>
    void DeleteChapter(
        ApplicationDbContext context,
        Chapter chapter,
        bool deleteNormal,
        bool deleteUpscaled
    );

    /// <summary>
    /// Deletes a manga from the library, optionally deleting all chapters.
    /// </summary>
    /// <param name="context">The database context to use.</param>
    /// <param name="manga">The manga to delete.</param>
    /// <param name="deleteNormal">Whether to delete all normal chapters.</param>
    /// <param name="deleteUpscaled">Whether to delete all upscaled chapters.</param>
    void DeleteManga(
        ApplicationDbContext context,
        Manga manga,
        bool deleteNormal,
        bool deleteUpscaled
    );
}
