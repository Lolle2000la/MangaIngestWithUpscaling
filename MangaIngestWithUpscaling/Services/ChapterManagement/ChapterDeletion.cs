﻿using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

[RegisterScoped]
public class ChapterDeletion(ApplicationDbContext dbContext,
    ILogger<ChapterDeletion> logger) : IChapterDeletion
{
    /// <inheritdoc/>
    public void DeleteChapter(Chapter chapter, bool deleteNormal, bool deleteUpscaled)
    {
        var normalPath = Path.Combine(chapter.Manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);
        if (deleteNormal && File.Exists(normalPath))
        {
            File.Delete(normalPath);
            var normalDir = Path.GetDirectoryName(normalPath);
            if (normalDir != null)
            {
                FileSystemHelpers.DeleteIfEmpty(normalDir, logger);
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
            if (upscaledDir != null)
            {
                FileSystemHelpers.DeleteIfEmpty(upscaledDir, logger);
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
