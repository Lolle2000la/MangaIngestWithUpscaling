using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

[RegisterScoped]
public class LibraryIntegrityChecker(
        ApplicationDbContext dbContext,
        IMetadataHandlingService metadataHandling,
        ILogger<LibraryIntegrityChecker> logger) : ILibraryIntegrityChecker
{
    public async Task CheckIntegrity(CancellationToken cancellationToken)
    {
        var libraries = await dbContext.Libraries
            .Include(l => l.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.OtherTitles)
            .ToListAsync(cancellationToken);

        foreach (var library in libraries)
        {
            await CheckIntegrity(library, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Library library, CancellationToken cancellationToken)
    {
        foreach (var manga in library.MangaSeries)
        {
            await CheckIntegrity(manga, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Manga manga, CancellationToken cancellationToken)
    {
        foreach (var chapter in manga.Chapters)
        {
            await CheckIntegrity(chapter, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Chapter chapter, CancellationToken cancellationToken)
    {
        await CheckOriginalIntegrity(chapter, cancellationToken);
        await CheckUpscaledIntegrity(chapter, cancellationToken);
    }

    private async Task CheckOriginalIntegrity(Chapter chapter, CancellationToken cancellationToken)
    {

    }

    private async Task CheckUpscaledIntegrity(Chapter chapter, CancellationToken cancellationToken)
    {
        var taskQuery = dbContext.PersistedTasks
            .FromSql($"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
        var tasks = await taskQuery.ToListAsync(cancellationToken);

        // ensure we find out whether one of the tasks is still pending or processing, otherwise we might find past tasks that superseeded.
        var task = tasks.FirstOrDefault(t =>
            t.Status == PersistedTaskStatus.Pending
            || t.Status == PersistedTaskStatus.Processing);

        if (task != null)
        {
            tasks.FirstOrDefault();
        }

        if (!chapter.IsUpscaled)
        {
            if (File.Exists(chapter.UpscaledFullPath))
            {
                // don't accidentally interfere with a running upscale.
                if (task?.Status != PersistedTaskStatus.Processing || task?.Status != PersistedTaskStatus.Pending)
                {
                    await CheckUpscaledArchiveValidity(chapter, cancellationToken);
                }
            }
        }
        else
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                logger.LogWarning("Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Marking as not upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = false;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await CheckUpscaledArchiveValidity(chapter, cancellationToken);
            }
        }
    }

    private async Task CheckUpscaledArchiveValidity(Chapter chapter, CancellationToken cancellationToken)
    {
        try
        {
            if (metadataHandling.PagesEqual(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath))
            {
                if (chapter.IsUpscaled)
                {
                    return;
                }
                logger.LogInformation("A seemingly valid upscale was found for {chapterFileName}({chapterId}) of {seriesTitle}. Marking chapter as upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = true;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                throw new InvalidDataException("The upscaled chapter does not match the outward number of pages to the original chapter.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "An invalid upscale was found for {chapterFileName} ({chapterId}) of {seriesTitle}, but no associated task to upscale was found. Deleting.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            try
            {
                if (File.Exists(chapter.UpscaledFullPath))
                    File.Delete(chapter.UpscaledFullPath);
                if (chapter.IsUpscaled)
                {
                    chapter.IsUpscaled = false;
                    dbContext.Update(chapter);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex2)
            {
                logger.LogError(ex2, "Failed to delete invalid upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            }
        }
    }
}
