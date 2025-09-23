using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using System.Threading;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

[RegisterScoped]
public class LibraryIntegrityChecker(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IMetadataHandlingService metadataHandling,
    ITaskQueue taskQueue,
    ILogger<LibraryIntegrityChecker> logger) : ILibraryIntegrityChecker
{
    private readonly int _maxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);
    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(CancellationToken? cancellationToken = null)
    {
        return await CheckIntegrity(new Progress<IntegrityProgress>(_ => { }), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null)
    {
        using var context = contextFactory.CreateDbContext();
        var libraries = await context.Libraries
            .Include(l => l.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.OtherTitles)
            .ToListAsync(cancellationToken ?? CancellationToken.None);

        // Compute total chapters across all libraries for deterministic progress
        int totalChapters = libraries.SelectMany(l => l.MangaSeries).Sum(m => m.Chapters.Count);
        int current = 0;
        int anyChanges = 0;
        progress.Report(new IntegrityProgress(totalChapters, current, "all", "Starting integrity check"));

        // Use library IDs for parallel processing
        var libraryIds = libraries.Select(l => l.Id).ToList();

        await Parallel.ForEachAsync(libraryIds, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken ?? CancellationToken.None
        }, async (libraryId, ct) =>
        {
            bool libraryChanged = await ProcessLibraryByIdAsync(libraryId, new Progress<IntegrityProgress>(p =>
            {
                // Bump global current when a chapter completes, ignore nested totals
                if (p.Scope == "chapter")
                {
                    Interlocked.Increment(ref current);
                }

                // Forward status with global scale
                progress.Report(new IntegrityProgress(totalChapters, current, p.Scope, p.StatusMessage));
            }), ct);

            if (libraryChanged)
            {
                Interlocked.Exchange(ref anyChanges, 1);
            }
        });

        progress.Report(new IntegrityProgress(totalChapters, totalChapters, "all", "Completed"));
        return anyChanges == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(Library library, CancellationToken? cancellationToken = null)
    {
        return await CheckIntegrity(library, new Progress<IntegrityProgress>(_ => { }), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(Library library, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null)
    {
        using var context = contextFactory.CreateDbContext();
        
        // Reload library with all necessary includes
        var loadedLibrary = await context.Libraries
            .Include(l => l.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.OtherTitles)
            .FirstOrDefaultAsync(l => l.Id == library.Id, cancellationToken ?? CancellationToken.None);

        if (loadedLibrary == null)
        {
            logger.LogWarning("Library with ID {LibraryId} not found during integrity check", library.Id);
            return false;
        }

        // Compute totals per-library and also report against a global context if provided by caller
        int totalChaptersInLibrary = loadedLibrary.MangaSeries.Sum(m => m.Chapters.Count);
        int currentInLibrary = 0;
        int anyChanges = 0;
        progress.Report(new IntegrityProgress(totalChaptersInLibrary, currentInLibrary, "library",
            $"Checking {loadedLibrary.Name}"));

        // Use manga IDs for parallel processing
        var mangaIds = loadedLibrary.MangaSeries.Select(m => m.Id).ToList();

        await Parallel.ForEachAsync(mangaIds, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken ?? CancellationToken.None
        }, async (mangaId, ct) =>
        {
            bool mangaChanged = await ProcessMangaByIdAsync(mangaId, new Progress<IntegrityProgress>(p =>
            {
                // Promote chapter-level increments
                if (p.Scope == "chapter")
                {
                    Interlocked.Increment(ref currentInLibrary);
                }

                progress.Report(new IntegrityProgress(totalChaptersInLibrary, currentInLibrary, p.Scope,
                    p.StatusMessage));
            }), ct);

            if (mangaChanged)
            {
                Interlocked.Exchange(ref anyChanges, 1);
            }
        });

        progress.Report(new IntegrityProgress(totalChaptersInLibrary, totalChaptersInLibrary, "library",
            $"Completed {loadedLibrary.Name}"));
        return anyChanges == 1;
    }

    /// <inheritdoc/>   
    public async Task<bool> CheckIntegrity(Manga manga, CancellationToken? cancellationToken = null)
    {
        return await CheckIntegrity(manga, new Progress<IntegrityProgress>(_ => { }), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(Manga manga, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null)
    {
        using var context = contextFactory.CreateDbContext();
        
        // Reload manga with all necessary includes
        var loadedManga = await context.MangaSeries
            .Include(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(m => m.OtherTitles)
            .Include(m => m.UpscalerProfilePreference)
            .Include(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(m => m.Id == manga.Id, cancellationToken ?? CancellationToken.None);

        if (loadedManga == null)
        {
            logger.LogWarning("Manga with ID {MangaId} not found during integrity check", manga.Id);
            return false;
        }

        int totalChapters = loadedManga.Chapters.Count;
        int current = 0;
        int anyChanges = 0;
        progress.Report(new IntegrityProgress(totalChapters, current, "manga", $"Checking {loadedManga.PrimaryTitle}"));

        // Use chapter IDs for parallel processing
        var chapterIds = loadedManga.Chapters.Select(c => c.Id).ToList();

        await Parallel.ForEachAsync(chapterIds, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken ?? CancellationToken.None
        }, async (chapterId, ct) =>
        {
            bool chapterChanged = await ProcessChapterByIdAsync(chapterId, new Progress<IntegrityProgress>(p =>
            {
                // Increment per chapter completion
                if (p.Scope == "chapter")
                {
                    Interlocked.Increment(ref current);
                    progress.Report(new IntegrityProgress(totalChapters, current, "chapter", p.StatusMessage));
                }
                else
                {
                    progress.Report(new IntegrityProgress(totalChapters, current, p.Scope, p.StatusMessage));
                }
            }), ct);

            if (chapterChanged)
            {
                Interlocked.Exchange(ref anyChanges, 1);
            }
        });

        progress.Report(new IntegrityProgress(totalChapters, totalChapters, "manga",
            $"Completed {loadedManga.PrimaryTitle}"));
        return anyChanges == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(Chapter chapter, CancellationToken? cancellationToken = null)
    {
        using var context = contextFactory.CreateDbContext();
        return await CheckIntegrityWithContext(context, chapter, cancellationToken);
    }

    /// <summary>
    /// Check integrity of a chapter using the provided DbContext.
    /// </summary>
    private async Task<bool> CheckIntegrityWithContext(ApplicationDbContext context, Chapter chapter, CancellationToken? cancellationToken = null)
    {
        try
        {
            var origIntegrity = await CheckOriginalIntegrityWithContext(context, chapter, cancellationToken);
            var upscaledIntegrity = IntegrityCheckResult.Ok;
            if (origIntegrity != IntegrityCheckResult.Missing && origIntegrity != IntegrityCheckResult.Invalid &&
                origIntegrity != IntegrityCheckResult.MaybeInProgress)
                upscaledIntegrity = await CheckUpscaledIntegrityWithContext(context, chapter, cancellationToken);

            if (origIntegrity != IntegrityCheckResult.Ok || upscaledIntegrity != IntegrityCheckResult.Ok)
            {
                logger.LogWarning(
                    "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues. Original: {origIntegrity}, Upscaled: {upscaledIntegrity}. Check the other log messages for more information on the cause of this.\n\nNote that this doesn't have to be a problem as many problems can and probably were corrected.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle, origIntegrity, upscaledIntegrity);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "An error occurred while checking integrity of chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            return true; // An error indicates a problem that needs attention
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(Chapter chapter, IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null)
    {
        progress.Report(new IntegrityProgress(null, null, "status", $"Checking {chapter.FileName}"));
        bool changed = await CheckIntegrity(chapter, cancellationToken);
        // Signal chapter completion; callers increment when Scope == "chapter"
        progress.Report(new IntegrityProgress(null, null, "chapter", $"Checked {chapter.FileName}"));
        return changed;
    }

    /// <summary>
    /// Process a library by ID using a fresh DbContext instance.
    /// </summary>
    private async Task<bool> ProcessLibraryByIdAsync(int libraryId, IProgress<IntegrityProgress> progress, CancellationToken cancellationToken)
    {
        using var context = contextFactory.CreateDbContext();
        
        var library = await context.Libraries
            .Include(l => l.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.OtherTitles)
            .FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken);

        if (library == null)
        {
            logger.LogWarning("Library with ID {LibraryId} not found during parallel processing", libraryId);
            return false;
        }

        progress.Report(new IntegrityProgress(null, null, "library", $"Checking {library.Name}"));
        return await CheckIntegrity(library, progress, cancellationToken);
    }

    /// <summary>
    /// Process a manga by ID using a fresh DbContext instance.
    /// </summary>
    private async Task<bool> ProcessMangaByIdAsync(int mangaId, IProgress<IntegrityProgress> progress, CancellationToken cancellationToken)
    {
        using var context = contextFactory.CreateDbContext();
        
        var manga = await context.MangaSeries
            .Include(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(m => m.OtherTitles)
            .Include(m => m.UpscalerProfilePreference)
            .Include(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(m => m.Id == mangaId, cancellationToken);

        if (manga == null)
        {
            logger.LogWarning("Manga with ID {MangaId} not found during parallel processing", mangaId);
            return false;
        }

        return await CheckIntegrity(manga, progress, cancellationToken);
    }

    /// <summary>
    /// Process a chapter by ID using a fresh DbContext instance.
    /// </summary>
    private async Task<bool> ProcessChapterByIdAsync(int chapterId, IProgress<IntegrityProgress> progress, CancellationToken cancellationToken)
    {
        using var context = contextFactory.CreateDbContext();
        
        var chapter = await context.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .Include(c => c.Manga)
            .ThenInclude(m => m.UpscalerProfilePreference)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

        if (chapter == null)
        {
            logger.LogWarning("Chapter with ID {ChapterId} not found during parallel processing", chapterId);
            return false;
        }

        progress.Report(new IntegrityProgress(null, null, "status", $"Checking {chapter.FileName}"));
        bool changed = await CheckIntegrityWithContext(context, chapter, cancellationToken);
        progress.Report(new IntegrityProgress(null, null, "chapter", $"Checked {chapter.FileName}"));
        return changed;
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
        using var context = contextFactory.CreateDbContext();
        return await CheckOriginalIntegrityWithContext(context, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckOriginalIntegrityWithContext(ApplicationDbContext context, Chapter chapter,
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

            context.Remove(chapter);
            try
            {
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);

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
        using var context = contextFactory.CreateDbContext();
        return await CheckUpscaledIntegrityWithContext(context, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrityWithContext(ApplicationDbContext context, Chapter chapter,
        CancellationToken? cancellationToken = null)
    {
        if (!chapter.IsUpscaled)
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                return IntegrityCheckResult.Ok;
            }

            IQueryable<PersistedTask> taskQuery = context.PersistedTasks
                .FromSql(
                    $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
            PersistedTask? task = await taskQuery.FirstOrDefaultAsync();

            // Do not modify the chapter from under the processing task.
            if (task != null)
            {
                return IntegrityCheckResult.MaybeInProgress;
            }

            return await CheckUpscaledArchiveValidityWithContext(context, chapter, cancellationToken);
        }
        else
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                logger.LogWarning(
                    "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Marking as not upscaled.",
                    chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                chapter.IsUpscaled = false;
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }
            else
            {
                return await CheckUpscaledArchiveValidityWithContext(context, chapter, cancellationToken);
            }
        }
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(Chapter chapter,
        CancellationToken? cancellationToken = null)
    {
        using var context = contextFactory.CreateDbContext();
        return await CheckUpscaledArchiveValidityWithContext(context, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidityWithContext(ApplicationDbContext context, Chapter chapter,
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
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
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
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Corrected;
            }
            else
            {
                // Analyze the differences to see if repair is possible
                PageDifferenceResult differences =
                    metadataHandling.AnalyzePageDifferences(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath);

                if (differences.CanRepair)
                {
                    logger.LogInformation(
                        "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues but can be repaired. Missing pages: {missingCount}, Extra pages: {extraCount}. Scheduling repair task.",
                        chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle, differences.MissingPages.Count,
                        differences.ExtraPages.Count);

                    // Check if a repair task is already queued for this chapter to avoid duplicates
                    IQueryable<PersistedTask> existingRepairTaskQuery = context.PersistedTasks
                        .FromSql(
                            $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(RepairUpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
                    PersistedTask? existingRepairTask =
                        await existingRepairTaskQuery.FirstOrDefaultAsync(cancellationToken ?? CancellationToken.None);

                    if (existingRepairTask == null)
                    {
                        // Schedule repair task instead of deleting
                        if (chapter.Manga?.EffectiveUpscalerProfile != null)
                        {
                            var repairTask = new RepairUpscaleTask(chapter, chapter.Manga.EffectiveUpscalerProfile);
                            await taskQueue.EnqueueAsync(repairTask);

                            logger.LogInformation(
                                "Scheduled repair task for chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);

                            return IntegrityCheckResult.Corrected;
                        }
                        else
                        {
                            logger.LogWarning(
                                "Cannot schedule repair for chapter {chapterFileName} ({chapterId}) of {seriesTitle} - no upscaler profile available",
                                chapter.FileName, chapter.Id, chapter.Manga?.PrimaryTitle);
                        }
                    }
                    else
                    {
                        logger.LogDebug(
                            "Repair task already exists for chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                        return IntegrityCheckResult.MaybeInProgress;
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues that cannot be repaired. Will fall back to deletion.",
                        chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                }

                throw new InvalidDataException(
                    "The upscaled chapter does not match the outward number of pages to the original chapter.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "An invalid upscale was found for {chapterFileName} ({chapterId}) of {seriesTitle}. Attempting repair before deletion.",
                chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
            try
            {
                // Try to analyze differences one more time in case the exception was from something else
                PageDifferenceResult differences =
                    metadataHandling.AnalyzePageDifferences(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath);

                if (differences.CanRepair && chapter.Manga?.EffectiveUpscalerProfile != null)
                {
                    // Check if a repair task is already queued for this chapter
                    IQueryable<PersistedTask> existingRepairTaskQuery = context.PersistedTasks
                        .FromSql(
                            $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(RepairUpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}");
                    PersistedTask? existingRepairTask =
                        await existingRepairTaskQuery.FirstOrDefaultAsync(cancellationToken ?? CancellationToken.None);

                    if (existingRepairTask == null)
                    {
                        var repairTask = new RepairUpscaleTask(chapter, chapter.Manga.EffectiveUpscalerProfile);
                        await taskQueue.EnqueueAsync(repairTask);

                        logger.LogInformation(
                            "Scheduled repair task for damaged chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);

                        return IntegrityCheckResult.Corrected;
                    }
                    else
                    {
                        logger.LogDebug(
                            "Repair task already exists for damaged chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName, chapter.Id, chapter.Manga.PrimaryTitle);
                        return IntegrityCheckResult.MaybeInProgress;
                    }
                }

                // Fall back to deletion if repair is not possible
                logger.LogWarning(
                    "Cannot repair chapter {chapterFileName} ({chapterId}) of {seriesTitle}. Falling back to deletion.",
                    chapter.FileName, chapter.Id, chapter.Manga?.PrimaryTitle);

                if (chapter.UpscaledFullPath != null && File.Exists(chapter.UpscaledFullPath))
                    File.Delete(chapter.UpscaledFullPath);
                if (chapter.IsUpscaled)
                {
                    chapter.IsUpscaled = false;
                    await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                }

                return IntegrityCheckResult.Invalid;
            }
            catch (Exception ex2)
            {
                logger.LogError(ex2,
                    "Failed to repair or delete invalid upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
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