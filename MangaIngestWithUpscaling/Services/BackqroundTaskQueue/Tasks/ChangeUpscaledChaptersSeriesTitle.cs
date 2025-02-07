
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class ChangeUpscaledChaptersSeriesTitle : BaseTask
{
    public override int RetryFor { get; set; } = 1;

    public required int ChapterId { get; set; }
    public string? ChapterFileName { get; set; }
    public required string NewTitle { get; set; }

    public ChangeUpscaledChaptersSeriesTitle() { }
    public ChangeUpscaledChaptersSeriesTitle(int chapterId, string chapterFileName, string newTitle)
    {
        ChapterId = chapterId;
        ChapterFileName = chapterFileName;
        NewTitle = newTitle;
    }

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
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
    }

    public override string TaskFriendlyName => $"Changing {ChapterFileName} title attribute to \"{NewTitle}\"";
}
