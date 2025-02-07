
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class RenameUpscaledChaptersSeriesTask : BaseTask
{
    public override int RetryFor { get; set; } = 1;

    public int ChapterId { get; set; }
    public string? ChapterFileName { get; set; }
    public string NewTitle { get; set; }

    public RenameUpscaledChaptersSeriesTask() { }
    public RenameUpscaledChaptersSeriesTask(int chapterId, string chapterFileName, string newTitle)
    {
        ChapterId = chapterId;
        ChapterFileName = chapterFileName;
        NewTitle = newTitle;
    }

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<RenameUpscaledChaptersSeriesTask>>();

        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var chapter = await dbContext.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(
            c => c.Id == ChapterId, cancellationToken: cancellationToken);

        if (chapter == null)
        {
            throw new InvalidOperationException("Chapter not found.");
        }

        if (chapter.Manga.Library.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException("Upscaled library path not set.");
        }

        string upscaleBasePath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, chapter.RelativePath);
        if (!File.Exists(upscaleBasePath))
        {
            throw new InvalidOperationException("Chapter file not found.");
        }

        var metadataHandling = services.GetRequiredService<IMetadataHandlingService>();

        var existingMetadata = metadataHandling.GetSeriesAndTitleFromComicInfo(upscaleBasePath);
        metadataHandling.WriteComicInfo(upscaleBasePath, existingMetadata with { Series = NewTitle });

        // move chapter to the correct directory with the new title
        var origChapterPath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, chapter.RelativePath);
        var newChapterPath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, NewTitle, chapter.FileName);
        var newRelativePath = Path.GetRelativePath(chapter.Manga.Library.UpscaledLibraryPath, newChapterPath);
        if (File.Exists(newChapterPath))
        {
            logger.LogWarning("Chapter file already exists: {ChapterPath}", newChapterPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(newChapterPath)!);
        File.Move(origChapterPath, newChapterPath);
        if (!Directory.EnumerateFiles(Path.GetDirectoryName(origChapterPath)!).Any())
        {
            Directory.Delete(Path.GetDirectoryName(origChapterPath)!);
        }
        chapter.RelativePath = newRelativePath;
        dbContext.Update(chapter);
    }

    public override string TaskFriendlyName => $"Changing {ChapterFileName} title attribute to \"{NewTitle}\"";
}
