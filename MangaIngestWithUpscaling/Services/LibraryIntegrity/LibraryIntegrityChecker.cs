using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

[RegisterScoped]
public class LibraryIntegrityChecker(
    ApplicationDbContext dbContext,
    IMetadataHandlingService metadataHandling,
    ITaskQueue taskQueue,
    ILogger<LibraryIntegrityChecker> logger
) : ILibraryIntegrityChecker
{
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
        var libraries = await dbContext
            .Libraries.Include(l => l.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.MangaSeries)
            .ThenInclude(m => m.OtherTitles)
            .ToListAsync(cancellationToken ?? CancellationToken.None);

        // Compute total chapters across all libraries for deterministic progress
        int totalChapters = libraries.SelectMany(l => l.MangaSeries).Sum(m => m.Chapters.Count);
        int current = 0;
        progress.Report(
            new IntegrityProgress(totalChapters, current, "all", "Starting integrity check")
        );

        bool changesHappened = false;

        foreach (var library in libraries.ToArray())
        {
            progress.Report(
                new IntegrityProgress(totalChapters, current, "library", $"Checking {library.Name}")
            );
            bool integrityCheckResult = await CheckIntegrity(
                library,
                new Progress<IntegrityProgress>(p =>
                {
                    // Bump global current when a chapter completes, ignore nested totals
                    if (p.Scope == "chapter")
                    {
                        current = Math.Min(totalChapters, current + 1);
                    }

                    // Forward status with global scale
                    progress.Report(
                        new IntegrityProgress(totalChapters, current, p.Scope, p.StatusMessage)
                    );
                }),
                cancellationToken
            );
            changesHappened = changesHappened || integrityCheckResult;
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
        return changesHappened;
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
        // Compute totals per-library and also report against a global context if provided by caller
        int totalChaptersInLibrary = library.MangaSeries.Sum(m => m.Chapters.Count);
        int currentInLibrary = 0;
        progress.Report(
            new IntegrityProgress(
                totalChaptersInLibrary,
                currentInLibrary,
                "library",
                $"Checking {library.Name}"
            )
        );

        bool changesHappened = false;

        foreach (var manga in library.MangaSeries.ToArray())
        {
            bool integrityCheckResult = await CheckIntegrity(
                manga,
                new Progress<IntegrityProgress>(p =>
                {
                    // Promote chapter-level increments
                    if (p.Scope == "chapter")
                    {
                        currentInLibrary = Math.Min(totalChaptersInLibrary, currentInLibrary + 1);
                    }

                    progress.Report(
                        new IntegrityProgress(
                            totalChaptersInLibrary,
                            currentInLibrary,
                            p.Scope,
                            p.StatusMessage
                        )
                    );
                }),
                cancellationToken
            );
            changesHappened = changesHappened || integrityCheckResult;
        }

        progress.Report(
            new IntegrityProgress(
                totalChaptersInLibrary,
                totalChaptersInLibrary,
                "library",
                $"Completed {library.Name}"
            )
        );
        return changesHappened;
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
        int totalChapters = manga.Chapters.Count;
        int current = 0;
        progress.Report(
            new IntegrityProgress(totalChapters, current, "manga", $"Checking {manga.PrimaryTitle}")
        );

        bool changesHappened = false;

        foreach (var chapter in manga.Chapters.ToArray())
        {
            bool integrityCheckResult = await CheckIntegrity(
                chapter,
                new Progress<IntegrityProgress>(p =>
                {
                    // Increment per chapter completion
                    if (p.Scope == "chapter")
                    {
                        current = Math.Min(totalChapters, current + 1);
                        progress.Report(
                            new IntegrityProgress(
                                totalChapters,
                                current,
                                "chapter",
                                p.StatusMessage
                            )
                        );
                    }
                    else
                    {
                        progress.Report(
                            new IntegrityProgress(totalChapters, current, p.Scope, p.StatusMessage)
                        );
                    }
                }),
                cancellationToken
            );
            changesHappened = changesHappened || integrityCheckResult;
        }

        progress.Report(
            new IntegrityProgress(
                totalChapters,
                totalChapters,
                "manga",
                $"Completed {manga.PrimaryTitle}"
            )
        );
        return changesHappened;
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

            dbContext.Remove(chapter);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);

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

        var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(chapter.NotUpscaledFullPath);
        if (!CheckMetadata(metadata, out var correctedMetadata))
        {
            logger.LogWarning(
                "Metadata of chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga.PrimaryTitle
            );
            metadataHandling.WriteComicInfo(chapter.NotUpscaledFullPath, correctedMetadata);
            return IntegrityCheckResult.Corrected;
        }

        return IntegrityCheckResult.Ok;
    }

    private async Task<IntegrityCheckResult> CheckUpscaledIntegrity(
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

            IQueryable<PersistedTask> taskQuery = dbContext.PersistedTasks.FromSql(
                $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(UpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}"
            );
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
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
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

    private async Task<IntegrityCheckResult> CheckUpscaledArchiveValidity(
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
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Missing;
            }

            if (metadataHandling.PagesEqual(chapter.NotUpscaledFullPath, chapter.UpscaledFullPath))
            {
                var metadata = metadataHandling.GetSeriesAndTitleFromComicInfo(
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
                    metadataHandling.WriteComicInfo(chapter.UpscaledFullPath, correctedMetadata);
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
                await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
                return IntegrityCheckResult.Corrected;
            }
            else
            {
                // Analyze the differences to see if repair is possible
                PageDifferenceResult differences = metadataHandling.AnalyzePageDifferences(
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
                    IQueryable<PersistedTask> existingRepairTaskQuery =
                        dbContext.PersistedTasks.FromSql(
                            $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(RepairUpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}"
                        );
                    PersistedTask? existingRepairTask =
                        await existingRepairTaskQuery.FirstOrDefaultAsync(
                            cancellationToken ?? CancellationToken.None
                        );

                    if (existingRepairTask == null)
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
                PageDifferenceResult differences = metadataHandling.AnalyzePageDifferences(
                    chapter.NotUpscaledFullPath,
                    chapter.UpscaledFullPath
                );

                if (differences.CanRepair && chapter.Manga?.EffectiveUpscalerProfile != null)
                {
                    // Check if a repair task is already queued for this chapter
                    IQueryable<PersistedTask> existingRepairTaskQuery =
                        dbContext.PersistedTasks.FromSql(
                            $"SELECT * FROM PersistedTasks WHERE Data->>'$.$type' = {nameof(RepairUpscaleTask)} AND Data->>'$.ChapterId' = {chapter.Id}"
                        );
                    PersistedTask? existingRepairTask =
                        await existingRepairTaskQuery.FirstOrDefaultAsync(
                            cancellationToken ?? CancellationToken.None
                        );

                    if (existingRepairTask == null)
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
                    await dbContext.SaveChangesAsync(cancellationToken ?? CancellationToken.None);
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

    private enum IntegrityCheckResult
    {
        Ok,
        Missing,
        Invalid,
        Corrected,
        MaybeInProgress,
    }
}
