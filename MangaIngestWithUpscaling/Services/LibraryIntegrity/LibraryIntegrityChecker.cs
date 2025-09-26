using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

[RegisterScoped]
public class LibraryIntegrityChecker(
    ApplicationDbContext dbContext,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IMetadataHandlingService metadataHandling,
    ITaskQueue taskQueue,
    ILogger<LibraryIntegrityChecker> logger,
    IOptions<IntegrityCheckerConfig> configOptions
) : ILibraryIntegrityChecker
{
    // Bound concurrency to avoid overloading disk/CPU and to keep services thread-safe
    private readonly int _maxDegreeOfParallelism = Math.Clamp(
        configOptions?.Value?.MaxParallelism ?? Environment.ProcessorCount,
        2,
        64
    );

    private readonly int _progressReportInterval = Math.Clamp(
        configOptions?.Value?.MaxParallelism ?? Environment.ProcessorCount,
        10,
        64
    );

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(CancellationToken? cancellationToken = null)
    {
        return await CheckIntegrity(new Progress<IntegrityProgress>(_ => { }), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(
        IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null
    )
    {
        CancellationToken ct = cancellationToken ?? CancellationToken.None;

        // Load libraries just for progress grouping; chapter processing will be parallelized per-library
        var libraries = await dbContext
            .Libraries.Include(l => l.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.OtherTitles)
            .ToListAsync(ct);

        int totalChapters = libraries.SelectMany(l => l.MangaSeries).Sum(m => m.Chapters.Count);
        int current = 0;
        progress.Report(
            new IntegrityProgress(totalChapters, current, "all", "Starting integrity check")
        );
        // Track any change flag across libraries in a thread-safe way
        int anyChange = 0;

        foreach (Library library in libraries)
        {
            progress.Report(
                new IntegrityProgress(totalChapters, current, "library", $"Checking {library.Name}")
            );

            bool libraryChanged = await CheckIntegrity(
                library,
                new Progress<IntegrityProgress>(p =>
                {
                    int currentAfter = current;
                    if (p.Scope == "chapter")
                    {
                        // Atomically increment the shared chapter counter; cap separately for reporting
                        currentAfter = Interlocked.Increment(ref current);
                    }

                    int cappedCurrent = currentAfter > totalChapters ? totalChapters : currentAfter;
                    progress.Report(
                        new IntegrityProgress(
                            totalChapters,
                            cappedCurrent,
                            p.Scope,
                            p.StatusMessage
                        )
                    );
                }),
                ct
            );

            if (libraryChanged)
            {
                Interlocked.Exchange(ref anyChange, 1);
            }

            progress.Report(
                new IntegrityProgress(
                    totalChapters,
                    current,
                    "library",
                    $"Completed {library.Name}"
                )
            );
        }

        progress.Report(new IntegrityProgress(totalChapters, totalChapters, "all", "Completed"));
        return anyChange == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(
        Library library,
        CancellationToken? cancellationToken = null
    )
    {
        return await CheckIntegrity(
            library,
            new Progress<IntegrityProgress>(_ => { }),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(
        Library library,
        IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null
    )
    {
        // Parallelize per-chapter within the library using fresh DbContexts per task
        // Compute totals via DB to avoid relying on loaded graphs
        int totalChaptersInLibrary = await dbContext
            .Chapters.Where(c => c.Manga!.Library!.Id == library.Id)
            .CountAsync(cancellationToken ?? CancellationToken.None);
        int currentInLibrary = 0;
        progress.Report(
            new IntegrityProgress(
                totalChaptersInLibrary,
                currentInLibrary,
                "library",
                $"Checking {library.Name}"
            )
        );

        IAsyncEnumerable<int> chapterIdsStream = dbContext
            .Chapters.AsNoTracking()
            .Where(c => c.Manga!.Library!.Id == library.Id)
            .Select(c => c.Id)
            .AsAsyncEnumerable();
        int anyChange = 0;
        CancellationToken ct = cancellationToken ?? CancellationToken.None;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(
            chapterIdsStream,
            parallelOptions,
            async (chapterId, token) =>
            {
                bool changed = await ProcessChapterByIdAsync(chapterId, token);
                if (changed)
                {
                    Interlocked.Exchange(ref anyChange, 1);
                }

                int curr = Interlocked.Increment(ref currentInLibrary);
                // Report progress every few chapters, and always at the last chapter
                if (curr % _progressReportInterval == 0 || curr == totalChaptersInLibrary)
                {
                    progress.Report(
                        new IntegrityProgress(
                            totalChaptersInLibrary,
                            curr,
                            "chapter",
                            $"Checked chapter {chapterId} in {library.Name}"
                        )
                    );
                }
            }
        );

        progress.Report(
            new IntegrityProgress(
                totalChaptersInLibrary,
                totalChaptersInLibrary,
                "library",
                $"Completed {library.Name}"
            )
        );
        return anyChange == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(Manga manga, CancellationToken? cancellationToken = null)
    {
        return await CheckIntegrity(
            manga,
            new Progress<IntegrityProgress>(_ => { }),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(
        Manga manga,
        IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null
    )
    {
        int totalChapters = await dbContext
            .Chapters.Where(c => c.MangaId == manga.Id)
            .CountAsync(cancellationToken ?? CancellationToken.None);
        int current = 0;
        progress.Report(
            new IntegrityProgress(totalChapters, current, "manga", $"Checking {manga.PrimaryTitle}")
        );

        IAsyncEnumerable<int> chapterIdsStream = dbContext
            .Chapters.AsNoTracking()
            .Where(c => c.MangaId == manga.Id)
            .Select(c => c.Id)
            .AsAsyncEnumerable();
        int anyChange = 0;
        CancellationToken ct = cancellationToken ?? CancellationToken.None;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(
            chapterIdsStream,
            parallelOptions,
            async (chapterId, token) =>
            {
                bool changed = await ProcessChapterByIdAsync(chapterId, token);
                if (changed)
                {
                    Interlocked.Exchange(ref anyChange, 1);
                }

                int curr = Interlocked.Increment(ref current);
                progress.Report(
                    new IntegrityProgress(
                        totalChapters,
                        curr,
                        "chapter",
                        $"Checked chapter {chapterId} in {manga.PrimaryTitle}"
                    )
                );
            }
        );

        progress.Report(
            new IntegrityProgress(
                totalChapters,
                totalChapters,
                "manga",
                $"Completed {manga.PrimaryTitle}"
            )
        );
        return anyChange == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckIntegrity(
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        try
        {
            var origIntegrity = await CheckOriginalIntegrity(chapter, cancellationToken);
            var upscaledIntegrity = IntegrityCheckResult.Ok;
            if (
                origIntegrity != IntegrityCheckResult.Missing
                && origIntegrity != IntegrityCheckResult.Invalid
                && origIntegrity != IntegrityCheckResult.MaybeInProgress
            )
                upscaledIntegrity = await CheckUpscaledIntegrity(chapter, cancellationToken);

            if (
                origIntegrity != IntegrityCheckResult.Ok
                || upscaledIntegrity != IntegrityCheckResult.Ok
            )
            {
                logger.LogWarning(
                    "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues. Original: {origIntegrity}, Upscaled: {upscaledIntegrity}. Check the other log messages for more information on the cause of this.\n\nNote that this doesn't have to be a problem as many problems can and probably were corrected.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle,
                    origIntegrity,
                    upscaledIntegrity
                );
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "An error occurred while checking integrity of chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga.PrimaryTitle
            );
            return true; // An error indicates a problem that needs attention
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckIntegrity(
        Chapter chapter,
        IProgress<IntegrityProgress> progress,
        CancellationToken? cancellationToken = null
    )
    {
        progress.Report(
            new IntegrityProgress(null, null, "status", $"Checking {chapter.FileName}")
        );
        bool changed = await CheckIntegrity(chapter, cancellationToken);
        // Signal chapter completion; callers increment when Scope == "chapter"
        progress.Report(
            new IntegrityProgress(null, null, "chapter", $"Checked {chapter.FileName}")
        );
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

    private async Task<IntegrityCheckResult> CheckOriginalIntegrity(
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        return await CheckOriginalIntegrity(dbContext, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckOriginalIntegrity(
        ApplicationDbContext context,
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        if (!File.Exists(chapter.NotUpscaledFullPath))
        {
            logger.LogWarning(
                "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Removing.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga.PrimaryTitle
            );

            if (chapter.IsUpscaled)
            {
                try
                {
                    if (File.Exists(chapter.UpscaledFullPath))
                        File.Delete(chapter.UpscaledFullPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to delete upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle
                    );
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
                logger.LogError(
                    ex,
                    "Failed to remove chapter {chapterFileName} ({chapterId}) of {seriesTitle} from database.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );

                return IntegrityCheckResult.Invalid;
            }
        }

        ExtractedMetadata metadata = await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(
            chapter.NotUpscaledFullPath
        );
        if (!CheckMetadata(metadata, out var correctedMetadata))
        {
            logger.LogWarning(
                "Metadata of chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga.PrimaryTitle
            );
            await metadataHandling.WriteComicInfoAsync(
                chapter.NotUpscaledFullPath,
                correctedMetadata
            );
            return IntegrityCheckResult.Corrected;
        }

        return IntegrityCheckResult.Ok;
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrity(
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        return await CheckUpscaledIntegrity(dbContext, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrity(
        ApplicationDbContext context,
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        if (!chapter.IsUpscaled)
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                return IntegrityCheckResult.Ok;
            }

            bool hasExistingUpscaleTask = await HasExistingUpscaleTaskAsync(
                context,
                chapter.Id,
                cancellationToken ?? CancellationToken.None
            );

            // Do not modify the chapter from under the processing task.
            if (hasExistingUpscaleTask)
            {
                return IntegrityCheckResult.MaybeInProgress;
            }

            return await CheckUpscaledArchiveValidity(context, chapter, cancellationToken);
        }
        else
        {
            if (!File.Exists(chapter.UpscaledFullPath))
            {
                logger.LogWarning(
                    "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing. Marking as not upscaled.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
                chapter.IsUpscaled = false;
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }
            else
            {
                return await CheckUpscaledArchiveValidity(context, chapter, cancellationToken);
            }
        }
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        return await CheckUpscaledArchiveValidity(dbContext, chapter, cancellationToken);
    }

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(
        ApplicationDbContext context,
        Chapter chapter,
        CancellationToken? cancellationToken = null
    )
    {
        try
        {
            if (chapter.UpscaledFullPath == null)
            {
                logger.LogWarning(
                    "Chapter {chapterFileName} ({chapterId}) of {seriesTitle} is missing a path. Marking as not upscaled.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
                chapter.IsUpscaled = false;
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }

            if (
                await metadataHandling.PagesEqualAsync(
                    chapter.NotUpscaledFullPath,
                    chapter.UpscaledFullPath
                )
            )
            {
                ExtractedMetadata metadata =
                    await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(
                        chapter.UpscaledFullPath
                    );
                if (!CheckMetadata(metadata, out var correctedMetadata))
                {
                    logger.LogWarning(
                        "Metadata of upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle
                    );
                    await metadataHandling.WriteComicInfoAsync(
                        chapter.UpscaledFullPath,
                        correctedMetadata
                    );
                }

                if (chapter.IsUpscaled)
                {
                    return IntegrityCheckResult.Ok;
                }

                logger.LogInformation(
                    "A seemingly valid upscale was found for {chapterFileName}({chapterId}) of {seriesTitle}. Marking chapter as upscaled.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
                chapter.IsUpscaled = true;
                await context.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Corrected;
            }
            else
            {
                // Analyze the differences to see if repair is possible
                PageDifferenceResult differences =
                    await metadataHandling.AnalyzePageDifferencesAsync(
                        chapter.NotUpscaledFullPath,
                        chapter.UpscaledFullPath
                    );

                if (differences.CanRepair)
                {
                    logger.LogInformation(
                        "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues but can be repaired. Missing pages: {missingCount}, Extra pages: {extraCount}. Scheduling repair task.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle,
                        differences.MissingPages.Count,
                        differences.ExtraPages.Count
                    );

                    // Check if a repair task is already queued for this chapter to avoid duplicates
                    bool hasExistingRepairTask = await HasExistingRepairTaskAsync(
                        context,
                        chapter.Id,
                        cancellationToken ?? CancellationToken.None
                    );

                    if (!hasExistingRepairTask)
                    {
                        // Schedule repair task instead of deleting
                        if (chapter.Manga?.EffectiveUpscalerProfile != null)
                        {
                            var repairTask = new RepairUpscaleTask(
                                chapter,
                                chapter.Manga.EffectiveUpscalerProfile
                            );
                            await taskQueue.EnqueueAsync(repairTask);

                            logger.LogInformation(
                                "Scheduled repair task for chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                                chapter.FileName,
                                chapter.Id,
                                chapter.Manga.PrimaryTitle
                            );

                            return IntegrityCheckResult.Corrected;
                        }
                        else
                        {
                            logger.LogWarning(
                                "Cannot schedule repair for chapter {chapterFileName} ({chapterId}) of {seriesTitle} - no upscaler profile available",
                                chapter.FileName,
                                chapter.Id,
                                chapter.Manga?.PrimaryTitle
                            );
                        }
                    }
                    else
                    {
                        logger.LogDebug(
                            "Repair task already exists for chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName,
                            chapter.Id,
                            chapter.Manga.PrimaryTitle
                        );
                        return IntegrityCheckResult.MaybeInProgress;
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle} has integrity issues that cannot be repaired. Will fall back to deletion.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle
                    );
                }

                throw new InvalidDataException(
                    "The upscaled chapter does not match the outward number of pages to the original chapter."
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "An invalid upscale was found for {chapterFileName} ({chapterId}) of {seriesTitle}. Attempting repair before deletion.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga.PrimaryTitle
            );
            try
            {
                // Try to analyze differences one more time in case the exception was from something else
                PageDifferenceResult differences =
                    await metadataHandling.AnalyzePageDifferencesAsync(
                        chapter.NotUpscaledFullPath,
                        chapter.UpscaledFullPath
                    );

                if (differences.CanRepair && chapter.Manga?.EffectiveUpscalerProfile != null)
                {
                    // Check if a repair task is already queued for this chapter
                    bool hasExistingRepairTask = await HasExistingRepairTaskAsync(
                        context,
                        chapter.Id,
                        cancellationToken ?? CancellationToken.None
                    );

                    if (!hasExistingRepairTask)
                    {
                        var repairTask = new RepairUpscaleTask(
                            chapter,
                            chapter.Manga.EffectiveUpscalerProfile
                        );
                        await taskQueue.EnqueueAsync(repairTask);

                        logger.LogInformation(
                            "Scheduled repair task for damaged chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName,
                            chapter.Id,
                            chapter.Manga.PrimaryTitle
                        );

                        return IntegrityCheckResult.Corrected;
                    }
                    else
                    {
                        logger.LogDebug(
                            "Repair task already exists for damaged chapter {chapterFileName} ({chapterId}) of {seriesTitle}",
                            chapter.FileName,
                            chapter.Id,
                            chapter.Manga.PrimaryTitle
                        );
                        return IntegrityCheckResult.MaybeInProgress;
                    }
                }

                // Fall back to deletion if repair is not possible
                logger.LogWarning(
                    "Cannot repair chapter {chapterFileName} ({chapterId}) of {seriesTitle}. Falling back to deletion.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga?.PrimaryTitle
                );

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
                logger.LogError(
                    ex2,
                    "Failed to repair or delete invalid upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
                return IntegrityCheckResult.Invalid;
            }
        }
    }

    // Process a single chapter by ID with its own DbContext for safe parallelization
    private async Task<bool> ProcessChapterByIdAsync(
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        // Load the chapter with minimal required relationships for logging and repair decisions
        var chapter = await context
            .Chapters.Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .Include(c => c.Manga)
            .ThenInclude(m => m.UpscalerProfilePreference)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

        if (chapter == null)
        {
            // Nothing to do; treat as no changes
            return false;
        }

        try
        {
            IntegrityCheckResult origIntegrity = await CheckOriginalIntegrity(
                context,
                chapter,
                cancellationToken
            );
            var upscaledIntegrity = IntegrityCheckResult.Ok;
            if (
                origIntegrity != IntegrityCheckResult.Missing
                && origIntegrity != IntegrityCheckResult.Invalid
                && origIntegrity != IntegrityCheckResult.MaybeInProgress
            )
            {
                upscaledIntegrity = await CheckUpscaledIntegrity(
                    context,
                    chapter,
                    cancellationToken
                );
            }

            return origIntegrity != IntegrityCheckResult.Ok
                || upscaledIntegrity != IntegrityCheckResult.Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "An error occurred while checking integrity of chapter {chapterId}.",
                chapterId
            );
            return true; // conservative: treat as needing attention
        }
    }

    private static async Task<bool> HasExistingUpscaleTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        IQueryable<PersistedTask> query = context.PersistedTasks.FromSql(
            $@"
            SELECT * FROM PersistedTasks
            WHERE Data->>'$.$type' = {nameof(UpscaleTask)}
              AND Data->>'$.ChapterId' = {chapterId}
        "
        );
        return await query.AnyAsync(cancellationToken);
    }

    private static async Task<bool> HasExistingRepairTaskAsync(
        ApplicationDbContext context,
        int chapterId,
        CancellationToken cancellationToken
    )
    {
        IQueryable<PersistedTask> query = context.PersistedTasks.FromSql(
            $@"
            SELECT * FROM PersistedTasks
            WHERE Data->>'$.$type' = {nameof(RepairUpscaleTask)}
              AND Data->>'$.ChapterId' = {chapterId}
        "
        );
        return await query.AnyAsync(cancellationToken);
    }

    private enum IntegrityCheckResult
    {
        Ok,
        Missing,
        Invalid,
        Corrected,
        MaybeInProgress,
    }
}
