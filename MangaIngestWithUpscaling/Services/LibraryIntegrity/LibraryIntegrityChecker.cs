using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
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
        if (origIntegrity != IntegrityCheckResult.Missing && origIntegrity != IntegrityCheckResult.Invalid &&
            origIntegrity != IntegrityCheckResult.MaybeInProgress)
            upscaledIntegrity = await CheckUpscaledIntegrity(chapter, cancellationToken);

        if (origIntegrity != IntegrityCheckResult.Ok || upscaledIntegrity != IntegrityCheckResult.Ok)
        {
            logger.LogWarning(
                "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues. Original: {origIntegrity}, Upscaled: {upscaledIntegrity}. Check the other log messages for more information on the cause of this.\n\nNote that this doesn't have to be a problem as many problems can and probably were corrected.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle, origIntegrity, upscaledIntegrity);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks metadata and corrects it if necessary.
    /// </summary>
    /// <param name="metadata">The metadata to check.</param>
    /// <param name="corrected">The possibly corrected metadata</param>
    /// <returns><c>true</c> if no changes necessary, <c>false</c> if something has was corrected.</returns>
    private bool CheckMetadata(ExtractedMetadata metadata, out ExtractedMetadata corrected)
    {
        var correctedMetadata = metadata.CheckAndCorrect();
        if (correctedMetadata != metadata)
        {
            corrected = correctedMetadata;
            return false;
        }

        corrected = metadata;
        return true;
    }

    private async Task<IntegrityCheckResult> CheckOriginalIntegrity(Chapter chapter,
        CancellationToken? cancellationToken = null)
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
                    logger.LogError(ex,
                        "Failed to delete upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
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
                logger.LogError(ex,
                    "Failed to remove chapter {chapterFileName} ({chapterId}) of {seriesTitle} from database.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);

                return IntegrityCheckResult.Invalid;
            }
        }

        var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapter.NotUpscaledFullPath);
        if (!CheckMetadata(metadata, out var correctedMetadata))
        {
            logger.LogWarning(
                "Metadata of chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            metadataHandling.WriteComicInfo(chapter.NotUpscaledFullPath, correctedMetadata);
            return IntegrityCheckResult.Corrected;
        }

        return IntegrityCheckResult.Ok;
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrity(Chapter chapter,
        CancellationToken? cancellationToken = null)
    {
        if (!chapter.IsUpscaled)
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                return IntegrityCheckResult.Ok;
            }

            IQueryable<PersistedTask> taskQuery = dbContext.PersistedTasks
                .FromSql(
                    $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
            PersistedTask? task = await taskQuery.FirstOrDefaultAsync();

            // Do not modify the chapter from under the processing task.
            if (task != null)
            {
                return IntegrityCheckResult.MaybeInProgress;
            }

            return await CheckUpscaledArchiveValidity(chapter, cancellationToken);
        }
        else
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                logger.LogWarning(
                    "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Marking as not upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = false;
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }
            else
            {
                return await CheckUpscaledArchiveValidity(chapter, cancellationToken);
            }
        }
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(Chapter chapter,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            if (chapter.UpscaledFullPath == null)
            {
                logger.LogWarning(
                    "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing a path. Marking as not upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = false;
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }

            if (metadataHandling.PagesEqual(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath))
            {
                var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapter.UpscaledFullPath);
                if (!CheckMetadata(metadata, out var correctedMetadata))
                {
                    logger.LogWarning(
                        "Metadata of upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                        chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                    metadataHandling.WriteComicInfo(chapter.UpscaledFullPath, correctedMetadata);
                }

                if (chapter.IsUpscaled)
                {
                    return IntegrityCheckResult.Ok;
                }

                logger.LogInformation(
                    "A seemingly valid upscale was found for {chapterFileName}({chapterId}) of {seriesTitle}. Marking chapter as upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = true;
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Corrected;
            }
            else
            {
                throw new InvalidDataException(
                    "The upscaled chapter does not match the outward number of pages to the original chapter.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "An invalid upscale was found for {chapterFileName} ({chapterId}) of {seriesTitle}, but no associated task to upscale was found. Deleting.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            try
            {
                if (chapter.UpscaledFullPath != null && File.Exists(chapter.UpscaledFullPath))
                    File.Delete(chapter.UpscaledFullPath);
                if (chapter.IsUpscaled)
                {
                    chapter.IsUpscaled = false;
                    await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                }

                return IntegrityCheckResult.Invalid;
            }
            catch (Exception ex2)
            {
                logger.LogError(ex2,
                    "Failed to delete invalid upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                return IntegrityCheckResult.Invalid;
            }
        }
    }

    private enum IntegrityCheckResult
    {
        Ok,
        Missing,
        Invalid,
        Corrected,
        MaybeInProgress
    }
}