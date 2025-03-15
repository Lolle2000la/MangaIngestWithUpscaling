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
    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(CancellationToken? cancellationToken = null)
    {
        var libraries = await dbContext.Libraries
            .Include(l => l.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.OtherTitles)
            .ToListAsync(cancellationToken ?? CancellationToken.None);

        bool changesHappened = false;

        foreach (var library in libraries.ToArray())
        {
            changesHappened = changesHappened || await CheckIntegrity(library, cancellationToken);
        }

        return changesHappened;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(Library library, CancellationToken? cancellationToken = null)
    {
        bool changesHappened = false;

        foreach (var manga in library.MangaSeries.ToArray())
        {
            changesHappened = changesHappened || await CheckIntegrity(manga, cancellationToken);
        }

        return changesHappened;
    }

    /// <inheritdoc/>   
    public async Task<bool> CheckIntegrity(Manga manga, CancellationToken? cancellationToken = null)
    {
        bool changesHappened = false;

        foreach (var chapter in manga.Chapters.ToArray())
        {
            changesHappened = changesHappened || await CheckIntegrity(chapter, cancellationToken);
        }

        return changesHappened;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(Chapter chapter, CancellationToken? cancellationToken = null)
    {
        var origIntegrity = await CheckOriginalIntegrity(chapter, cancellationToken);
        var upscaledIntegrity = IntegrityCheckResult.Ok;
        if (origIntegrity != IntegrityCheckResult.Missing && origIntegrity != IntegrityCheckResult.Invalid)
            upscaledIntegrity = await CheckUpscaledIntegrity(chapter, cancellationToken);

        if (origIntegrity != IntegrityCheckResult.Ok || upscaledIntegrity != IntegrityCheckResult.Ok)
        {
            logger.LogWarning("Chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues. Original: {origIntegrity}, Upscaled: {upscaledIntegrity}. Check the other log messages for more information on the cause of this.\n\nNote that this doesn't have to be a problem as many problems can and probably were corrected.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle, origIntegrity, upscaledIntegrity);
            return true;
        }

        return false;
    }

    private enum IntegrityCheckResult
    {
        Ok,
        Missing,
        Invalid,
        Corrected
    }

    private async Task<IntegrityCheckResult> CheckOriginalIntegrity(Chapter chapter, CancellationToken? cancellationToken = null)
    {
        if (!File.Exists(chapter.NotUpscaledFullPath))
        {
            logger.LogWarning("Chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Removing.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);

            if (chapter.IsUpscaled)
            {
                try
                {
                    if (File.Exists(chapter.UpscaledFullPath))
                        File.Delete(chapter.UpscaledFullPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                        chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                }
            }

            dbContext.Remove(chapter);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);

                return IntegrityCheckResult.Missing;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to remove chapter {chapterFileName} ({chapterId}) of {seriesTitle} from database.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);

                return IntegrityCheckResult.Invalid;
            }
        }

        return IntegrityCheckResult.Ok;
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrity(Chapter chapter, CancellationToken? cancellationToken = null)
    {
        if (!chapter.IsUpscaled)
        {
            if (File.Exists(chapter.UpscaledFullPath))
            {
                var taskQuery = dbContext.PersistedTasks
                    .FromSql($"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
                var tasks = await taskQuery.ToListAsync(cancellationToken ?? CancellationToken.None);

                // ensure we find out whether one of the tasks is still pending or processing, otherwise we might find past tasks that superseeded.
                var task = tasks.FirstOrDefault(t =>
                    t.Status == PersistedTaskStatus.Pending
                    || t.Status == PersistedTaskStatus.Processing);

                if (task != null)
                {
                    tasks.FirstOrDefault();
                }

                // don't accidentally interfere with a running upscale.
                if (task?.Status != PersistedTaskStatus.Processing && task?.Status != PersistedTaskStatus.Pending)
                {
                    return await CheckUpscaledArchiveValidity(chapter, cancellationToken);
                }
            }

            return IntegrityCheckResult.Ok;
        }
        else
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                logger.LogWarning("Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Marking as not upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = false;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }
            else
            {
                return await CheckUpscaledArchiveValidity(chapter, cancellationToken);
            }
        }
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(Chapter chapter, CancellationToken? cancellationToken = null)
    {
        try
        {
            if (metadataHandling.PagesEqual(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath))
            {
                if (chapter.IsUpscaled)
                {
                    return IntegrityCheckResult.Ok;
                }
                logger.LogInformation("A seemingly valid upscale was found for {chapterFileName}({chapterId}) of {seriesTitle}. Marking chapter as upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = true;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Corrected;
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
                    await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                }
                return IntegrityCheckResult.Invalid;
            }
            catch (Exception ex2)
            {
                logger.LogError(ex2, "Failed to delete invalid upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                return IntegrityCheckResult.Invalid;
            }
        }
    }
}
