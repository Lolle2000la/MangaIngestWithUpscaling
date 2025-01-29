using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.CbzConversion;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class ScanIngestTask : BaseTask
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;

    public virtual async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<ScanIngestTask>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var library = await dbContext.Libraries.FindAsync([LibraryId], cancellationToken: cancellationToken);

        if (library == null)
        {
            throw new InvalidOperationException($"Library with ID {LibraryId} not found.");
        }

        var chapterRecognitionService = services.GetRequiredService<IChapterInIngestRecognitionService>();
        List<FoundChapter> chapterRecognitionResult = chapterRecognitionService.FindAllChaptersAt(
            library.IngestPath, library.FilterRules);

        // group chapters by series
        var chaptersBySeries = chapterRecognitionResult.GroupBy(c => c.Metadata.Series).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (series, chapters) in chaptersBySeries)
        {
            // create series if it doesn't exist
            // Also take into account the alternate names
            var seriesEntity = await dbContext.MangaSeries
                .Include(s => s.OtherTitles)
                .FirstOrDefaultAsync(s => s.PrimaryTitle == series || s.OtherTitles.Any(an => an.Title == series),
                    cancellationToken: cancellationToken);

            if (seriesEntity == null)
            {
                seriesEntity = new Manga
                {
                    PrimaryTitle = series,
                    OtherTitles = new List<MangaAlternativeTitle>()
                };
                dbContext.MangaSeries.Add(seriesEntity);
            }

            var cbzConverter = services.GetRequiredService<ICbzConverter>();

            // Move chapters to the target path in file system as specified by the libraries NotUpscaledLibraryPath property.
            // Then create a Chapter entity for each chapter and add it to the series.
            foreach (var chapter in chapters)
            {
                FoundChapter chapterCbz;
                try
                {
                    chapterCbz = cbzConverter.ConvertToCbz(chapter, library.IngestPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error converting chapter {chapter} to cbz.", chapter.RelativePath);
                    continue;
                }
                var targetPath = Path.Combine(library.NotUpscaledLibraryPath, seriesEntity.PrimaryTitle!, chapterCbz.FileName);
                if (File.Exists(targetPath))
                {
                    logger.LogWarning("Chapter {fileName} already exists in the target path {targetPath}. Skipping.",
                         chapterCbz.FileName, targetPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(Path.Combine(library.IngestPath, chapter.RelativePath), targetPath);
                var chapterEntity = new Chapter
                {
                    FileName = chapterCbz.FileName,
                    MangaId = seriesEntity.Id,
                    RelativePath = targetPath,
                    IsUpscaled = false
                };
                seriesEntity.Chapters.Add(chapterEntity);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

    }
    public virtual string TaskFriendlyName { get; } = $"Scanning ";
}
