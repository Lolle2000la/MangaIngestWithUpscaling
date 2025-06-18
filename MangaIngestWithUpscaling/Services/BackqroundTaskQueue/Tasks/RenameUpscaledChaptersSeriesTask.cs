using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class RenameUpscaledChaptersSeriesTask : BaseTask
{
    public RenameUpscaledChaptersSeriesTask() { }

    public RenameUpscaledChaptersSeriesTask(int chapterId, string chapterFullPath, string newTitle)
    {
        ChapterId = chapterId;
        ChapterFileName = Path.GetFileName(chapterFullPath);
        ChapterFullPath = chapterFullPath;
        NewTitle = newTitle;
    }

    public override int RetryFor { get; set; } = 0;

    public int ChapterId { get; set; }
    public string? ChapterFileName { get; set; }

    /// <summary>
    /// Full path to the chapter file.
    /// This is necessary if the rename operation comes after moving the chapter to a new library.
    /// This might especially be the case if the manga was moved to another library or merged with another manga in a different library.
    /// </summary>
    public string ChapterFullPath { get; set; } = string.Empty;

    public string NewTitle { get; set; } = string.Empty;

    public override string TaskFriendlyName => $"Changing {ChapterFileName} title attribute to \"{NewTitle}\"";

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<RenameUpscaledChaptersSeriesTask>>();
        var fileSystem = services.GetRequiredService<IFileSystem>();

        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        Chapter? chapter = await dbContext.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(
                c => c.Id == ChapterId, cancellationToken);

        if (chapter == null)
        {
            throw new InvalidOperationException("Chapter not found.");
        }

        if (!chapter.IsUpscaled)
        {
            return;
        }

        if (chapter.Manga?.Library?.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException("Upscaled library path not set.");
        }

        string origChapterPath = ChapterFullPath;
        if (!File.Exists(origChapterPath))
        {
            throw new InvalidOperationException("Chapter file not found.");
        }

        var metadataChange = services.GetRequiredService<IMangaMetadataChanger>();

        metadataChange.ApplyMangaTitleToUpscaled(chapter, NewTitle, origChapterPath);
    }
}