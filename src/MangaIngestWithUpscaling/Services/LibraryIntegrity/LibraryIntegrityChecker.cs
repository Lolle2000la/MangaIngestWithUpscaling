using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.CbzConversion;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrity;

[RegisterScoped]
public partial class LibraryIntegrityChecker(
    ApplicationDbContext dbContext,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IMetadataHandlingService metadataHandling,
    IChapterInIngestRecognitionService chapterRecognitionService,
    IChapterProcessingService chapterProcessingService,
    ITaskQueue taskQueue,
    ICbzConverter cbzConverter,
    ILogger<LibraryIntegrityChecker> logger,
    IOptions<IntegrityCheckerConfig> configOptions,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    IStringLocalizer<LibraryIntegrityChecker> localizer
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

    private readonly bool _fixImageExtensions = configOptions?.Value?.FixImageExtensions ?? false;

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

        // Check for orphaned files after processing existing chapters
        bool orphanedFilesFound = await CheckForOrphanedFiles(
            library,
            progress,
            totalChaptersInLibrary,
            ct
        );
        if (orphanedFilesFound)
        {
            Interlocked.Exchange(ref anyChange, 1);
        }

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

        // Fix image extensions if needed and enabled in configuration
        bool extensionsFixed = false;
        if (_fixImageExtensions)
        {
            try
            {
                if (cbzConverter.FixImageExtensionsInCbz(chapter.NotUpscaledFullPath))
                {
                    logger.LogInformation(
                        "Fixed image file extensions in original chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle
                    );
                    extensionsFixed = true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to fix image extensions in original chapter {chapterFileName} ({chapterId}) of {seriesTitle}. Continuing with other checks.",
                    chapter.FileName,
                    chapter.Id,
                    chapter.Manga.PrimaryTitle
                );
            }
        }

        ExtractedMetadata metadata = await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(
            chapter.NotUpscaledFullPath
        );

        // Check if split detection is needed
        if (
            chapter.Manga?.Library?.StripDetectionMode is { } mode
            && mode != Shared.Data.Analysis.StripDetectionMode.None
        )
        {
            if (
                await splitProcessingCoordinator.ShouldProcessAsync(
                    chapter.Id,
                    mode,
                    context,
                    cancellationToken ?? CancellationToken.None
                )
            )
            {
                await splitProcessingCoordinator.EnqueueDetectionAsync(
                    chapter.Id,
                    cancellationToken ?? CancellationToken.None
                );
            }
        }

        if (!CheckMetadata(metadata, out var correctedMetadata))
        {
            logger.LogWarning(
                "Metadata of chapter {chapterFileName} ({chapterId}) of {seriesTitle} is incorrect. Correcting.",
                chapter.FileName,
                chapter.Id,
                chapter.Manga?.PrimaryTitle
            );
            await metadataHandling.WriteComicInfoAsync(
                chapter.NotUpscaledFullPath,
                correctedMetadata
            );
            return IntegrityCheckResult.Corrected;
        }

        return extensionsFixed ? IntegrityCheckResult.Corrected : IntegrityCheckResult.Ok;
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

            // Fix image extensions in upscaled file if needed and enabled in configuration
            bool upscaledExtensionsFixed = false;
            if (_fixImageExtensions)
            {
                try
                {
                    if (cbzConverter.FixImageExtensionsInCbz(chapter.UpscaledFullPath))
                    {
                        logger.LogInformation(
                            "Fixed image file extensions in upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}.",
                            chapter.FileName,
                            chapter.Id,
                            chapter.Manga.PrimaryTitle
                        );
                        upscaledExtensionsFixed = true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to fix image extensions in upscaled chapter {chapterFileName} ({chapterId}) of {seriesTitle}. Continuing with other checks.",
                        chapter.FileName,
                        chapter.Id,
                        chapter.Manga.PrimaryTitle
                    );
                }
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
                bool metadataCorrected = false;
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
                    metadataCorrected = true;
                }

                if (chapter.IsUpscaled)
                {
                    return (upscaledExtensionsFixed || metadataCorrected)
                        ? IntegrityCheckResult.Corrected
                        : IntegrityCheckResult.Ok;
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

                throw new InvalidDataException(localizer["Error_UpscaledChapterPageCountMismatch"]);
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

    /// <summary>
    /// Checks for orphaned files in the library directories that don't have corresponding chapter entities.
    /// Creates missing chapter entities for valid chapter files found, properly handling original/upscaled file pairs.
    /// </summary>
    /// <param name="library">The library to check for orphaned files</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="baseProgressCount">Base count for progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if any orphaned files were found and entities were created</returns>
    private async Task<bool> CheckForOrphanedFiles(
        Library library,
        IProgress<IntegrityProgress> progress,
        int baseProgressCount,
        CancellationToken cancellationToken
    )
    {
        progress.Report(
            new IntegrityProgress(
                baseProgressCount,
                baseProgressCount,
                "library",
                $"Checking for orphaned files in {library.Name}"
            )
        );

        bool anyChanges = false;

        // Collect all found chapters from both upscaled and non-upscaled paths
        var allFoundChapters = new List<(FoundChapter Chapter, bool IsFromUpscaledPath)>();

        // Check NotUpscaled library path
        if (Directory.Exists(library.NotUpscaledLibraryPath))
        {
            try
            {
                var foundChapters = chapterRecognitionService.FindAllChaptersAt(
                    library.NotUpscaledLibraryPath,
                    null,
                    cancellationToken
                );
                await foreach (var foundChapter in foundChapters)
                {
                    allFoundChapters.Add((foundChapter, false));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Error checking for orphaned files in NotUpscaled path {LibraryPath} for library {LibraryName}",
                    library.NotUpscaledLibraryPath,
                    library.Name
                );
            }
        }

        // Check Upscaled library path if it exists
        if (
            !string.IsNullOrEmpty(library.UpscaledLibraryPath)
            && Directory.Exists(library.UpscaledLibraryPath)
        )
        {
            try
            {
                var foundChapters = chapterRecognitionService.FindAllChaptersAt(
                    library.UpscaledLibraryPath,
                    null,
                    cancellationToken
                );
                await foreach (var foundChapter in foundChapters)
                {
                    allFoundChapters.Add((foundChapter, true));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Error checking for orphaned files in Upscaled path {LibraryPath} for library {LibraryName}",
                    library.UpscaledLibraryPath,
                    library.Name
                );
            }
        }

        // Group files by their canonical path (treating original and upscaled as variants of the same chapter)
        var chapterGroups = await GroupChaptersByCanonicalPath(
            allFoundChapters,
            library,
            cancellationToken
        );

        // Process each chapter group
        foreach (var group in chapterGroups)
        {
            // Check if a chapter entity already exists for this canonical path
            var existingChapter = await dbContext
                .Chapters.Include(c => c.Manga)
                .Where(c => c.Manga.LibraryId == library.Id)
                .FirstOrDefaultAsync(
                    c => c.RelativePath == group.CanonicalRelativePath,
                    cancellationToken
                );

            if (existingChapter == null)
            {
                // This is an orphaned chapter - create a chapter entity for it
                anyChanges =
                    await CreateChapterEntityForOrphanedChapterGroup(
                        library,
                        group,
                        cancellationToken
                    ) || anyChanges;
            }
            else if (group.UpscaledChapter != null && !existingChapter.IsUpscaled)
            {
                // We found an upscaled file for an existing chapter that's not marked as upscaled
                anyChanges =
                    await HandleUpscaledFileForExistingChapter(
                        library,
                        group,
                        existingChapter,
                        cancellationToken
                    ) || anyChanges;
            }
        }

        return anyChanges;
    }

    /// <summary>
    /// Groups found chapters by their canonical path, combining original and upscaled variants.
    /// Also detects upscaled files by upscaler.json and handles file movement.
    /// </summary>
    private async Task<List<ChapterGroup>> GroupChaptersByCanonicalPath(
        List<(FoundChapter Chapter, bool IsFromUpscaledPath)> allFoundChapters,
        Library library,
        CancellationToken cancellationToken
    )
    {
        var groups = new Dictionary<string, ChapterGroup>();

        foreach (var (chapter, isFromUpscaledPath) in allFoundChapters)
        {
            // Determine canonical path (remove _upscaled folder from path if present)
            string canonicalPath = ChapterProcessingService.GetCanonicalPath(chapter.RelativePath);

            if (!groups.TryGetValue(canonicalPath, out var group))
            {
                group = new ChapterGroup { CanonicalRelativePath = canonicalPath };
                groups[canonicalPath] = group;
            }

            if (isFromUpscaledPath)
            {
                group.UpscaledChapter = chapter;
                // Try to extract upscaler profile from the upscaled file
                string fullPath = Path.Combine(library.UpscaledLibraryPath!, chapter.RelativePath);
                var (_, upscalerProfile) = await chapterProcessingService.DetectUpscaledFileAsync(
                    fullPath,
                    chapter.RelativePath,
                    cancellationToken
                );
                group.UpscalerProfileDto = upscalerProfile;
            }
            else
            {
                // Use shared service to detect if this file is upscaled
                string fullPath = Path.Combine(
                    library.NotUpscaledLibraryPath,
                    chapter.RelativePath
                );
                var (isUpscaled, upscalerProfile) =
                    await chapterProcessingService.DetectUpscaledFileAsync(
                        fullPath,
                        chapter.RelativePath,
                        cancellationToken
                    );

                if (isUpscaled)
                {
                    // This is an upscaled file - it should be moved to upscaled folder if upscaler.json was found
                    group.UpscaledChapter = chapter;
                    group.UpscalerProfileDto = upscalerProfile;
                    group.RequiresFileMovement = upscalerProfile != null; // Only move files with upscaler.json
                    group.SourcePath = fullPath;
                }
                else
                {
                    group.OriginalChapter = chapter;
                }
            }
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// Gets the canonical path by removing _upscaled folder components.
    /// </summary>
    private static string GetCanonicalPath(string relativePath)
    {
        return ChapterProcessingService.GetCanonicalPath(relativePath);
    }

    /// <summary>
    /// Checks for duplicate chapter files and handles them by deleting the orphaned file if a duplicate exists.
    /// Uses both primary titles and alternative titles to detect duplicates.
    /// </summary>
    private async Task<bool> CheckAndHandleDuplicateChapterFiles(
        Library library,
        ChapterGroup group,
        FoundChapter chapterToUse,
        CancellationToken cancellationToken
    )
    {
        // Look for existing chapters with the same series title (including alternative titles)
        var existingChapters = await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.OtherTitles)
            .Where(c => c.Manga.LibraryId == library.Id)
            .Where(c =>
                c.Manga.PrimaryTitle == chapterToUse.Metadata.Series
                || c.Manga.OtherTitles.Any(at => at.Title == chapterToUse.Metadata.Series)
            )
            .Where(c =>
                c.FileName == chapterToUse.FileName || c.RelativePath == group.CanonicalRelativePath
            )
            .ToListAsync(cancellationToken);

        if (existingChapters.Any())
        {
            // Found duplicate - delete the orphaned file(s)
            try
            {
                if (group.OriginalChapter != null)
                {
                    string originalPath = Path.Combine(
                        library.NotUpscaledLibraryPath,
                        group.OriginalChapter.RelativePath
                    );
                    if (File.Exists(originalPath))
                    {
                        File.Delete(originalPath);
                        logger.LogInformation(
                            "Deleted orphaned duplicate original file '{FileName}' (existing chapter found)",
                            group.OriginalChapter.FileName
                        );
                    }
                }

                if (
                    group.UpscaledChapter != null
                    && !string.IsNullOrEmpty(library.UpscaledLibraryPath)
                )
                {
                    string upscaledPath =
                        group.RequiresFileMovement && group.SourcePath != null
                            ? group.SourcePath
                            : Path.Combine(
                                library.UpscaledLibraryPath,
                                group.UpscaledChapter.RelativePath
                            );

                    if (File.Exists(upscaledPath))
                    {
                        File.Delete(upscaledPath);
                        logger.LogInformation(
                            "Deleted orphaned duplicate upscaled file '{FileName}' (existing chapter found)",
                            group.UpscaledChapter.FileName
                        );
                    }
                }

                return true; // Duplicate was handled
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to delete orphaned duplicate file for chapter '{FileName}'",
                    chapterToUse.FileName
                );
                return false;
            }
        }

        return false; // No duplicate found
    }

    /// <summary>
    /// Moves an orphaned original file to the correct location in the library structure.
    /// </summary>
    private string MoveOrphanedOriginalFileToCorrectLocation(
        Library library,
        ChapterGroup group,
        Manga manga,
        CancellationToken cancellationToken
    )
    {
        if (group.OriginalChapter == null)
            return string.Empty;

        try
        {
            // Determine the correct target path in the library structure
            string sourcePath = Path.Combine(
                library.NotUpscaledLibraryPath,
                group.OriginalChapter.RelativePath
            );

            // Create target path: SeriesName/ChapterFile.ext
            string targetRelativePath = Path.Combine(
                manga.PrimaryTitle,
                group.OriginalChapter.FileName
            );
            string targetPath = Path.Combine(library.NotUpscaledLibraryPath, targetRelativePath);

            // Only move if source and target are different
            if (Path.GetFullPath(sourcePath) != Path.GetFullPath(targetPath))
            {
                // Ensure target directory exists
                string? targetDir = Path.GetDirectoryName(targetPath);
                if (targetDir != null && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Move the file
                if (File.Exists(sourcePath))
                {
                    if (File.Exists(targetPath))
                    {
                        // Target exists, delete source as it's a duplicate
                        File.Delete(sourcePath);
                        logger.LogInformation(
                            "Deleted orphaned duplicate original file '{FileName}' (target already exists)",
                            group.OriginalChapter.FileName
                        );
                    }
                    else
                    {
                        File.Move(sourcePath, targetPath);
                        logger.LogInformation(
                            "Moved orphaned original file '{FileName}' to correct location '{TargetPath}'",
                            group.OriginalChapter.FileName,
                            targetRelativePath
                        );
                    }
                }

                // Update the canonical relative path to match the new location
                group.CanonicalRelativePath = targetRelativePath;
            }

            return targetPath;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to move orphaned original file '{FileName}' to correct location",
                group.OriginalChapter.FileName
            );
            return Path.Combine(library.NotUpscaledLibraryPath, group.OriginalChapter.RelativePath);
        }
    }

    /// <summary>
    /// Fixes metadata in orphaned files to ensure correct series title and other metadata.
    /// </summary>
    private async Task FixOrphanedFileMetadata(
        Library library,
        ChapterGroup group,
        Manga manga,
        string finalOriginalPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Fix metadata in original file if it exists
            if (group.OriginalChapter != null && File.Exists(finalOriginalPath))
            {
                var originalMetadata = await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(
                    finalOriginalPath
                );

                // Check if metadata needs fixing (series title mismatch)
                if (originalMetadata.Series != manga.PrimaryTitle)
                {
                    var correctedMetadata = originalMetadata with { Series = manga.PrimaryTitle };
                    await metadataHandling.WriteComicInfoAsync(
                        finalOriginalPath,
                        correctedMetadata
                    );

                    logger.LogInformation(
                        "Fixed metadata in orphaned original file '{FileName}': series title '{OldTitle}' -> '{NewTitle}'",
                        group.OriginalChapter.FileName,
                        originalMetadata.Series,
                        manga.PrimaryTitle
                    );
                }
            }

            // Fix metadata in upscaled file if it exists
            if (group.UpscaledChapter != null && !string.IsNullOrEmpty(library.UpscaledLibraryPath))
            {
                string upscaledPath = Path.Combine(
                    library.UpscaledLibraryPath,
                    group.CanonicalRelativePath
                );
                if (File.Exists(upscaledPath))
                {
                    var upscaledMetadata =
                        await metadataHandling.GetSeriesAndTitleFromComicInfoAsync(upscaledPath);

                    // Check if metadata needs fixing (series title mismatch)
                    if (upscaledMetadata.Series != manga.PrimaryTitle)
                    {
                        var correctedMetadata = upscaledMetadata with
                        {
                            Series = manga.PrimaryTitle,
                        };
                        await metadataHandling.WriteComicInfoAsync(upscaledPath, correctedMetadata);

                        logger.LogInformation(
                            "Fixed metadata in orphaned upscaled file '{FileName}': series title '{OldTitle}' -> '{NewTitle}'",
                            group.UpscaledChapter.FileName,
                            upscaledMetadata.Series,
                            manga.PrimaryTitle
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to fix metadata for orphaned chapter group with canonical path '{CanonicalPath}'",
                group.CanonicalRelativePath
            );
        }
    }

    /// <summary>
    /// Creates a chapter entity for an orphaned chapter group, handling both original and upscaled variants.
    /// Moves files to correct locations, fixes metadata, and handles duplicate detection.
    /// </summary>
    private async Task<bool> CreateChapterEntityForOrphanedChapterGroup(
        Library library,
        ChapterGroup group,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Check if this is an upscaled-only group (no original file)
            if (group.OriginalChapter == null && group.UpscaledChapter != null)
            {
                // Check if there's an existing chapter entity that could be the original
                var existingOriginalChapter = await dbContext
                    .Chapters.Include(c => c.Manga)
                    .Where(c => c.Manga.LibraryId == library.Id)
                    .FirstOrDefaultAsync(
                        c => c.RelativePath == group.CanonicalRelativePath && !c.IsUpscaled,
                        cancellationToken
                    );

                if (existingOriginalChapter == null)
                {
                    logger.LogWarning(
                        "Found upscaled orphan file '{FileName}' without matching original file. Skipping until original is found.",
                        group.UpscaledChapter.FileName
                    );
                    return false; // Don't process upscaled files without original
                }
                else
                {
                    // We have an existing original chapter, just need to mark it as upscaled and handle file movement
                    return await HandleUpscaledFileForExistingChapter(
                        library,
                        group,
                        existingOriginalChapter,
                        cancellationToken
                    );
                }
            }

            // Use the original chapter's metadata if available, otherwise use upscaled chapter's metadata
            var chapterToUse = group.OriginalChapter ?? group.UpscaledChapter;
            if (chapterToUse == null)
            {
                logger.LogWarning(
                    "Chapter group with canonical path {CanonicalPath} has no chapters",
                    group.CanonicalRelativePath
                );
                return false;
            }

            // Check for duplicates by looking for existing chapters with same series (including alternative titles)
            if (
                await CheckAndHandleDuplicateChapterFiles(
                    library,
                    group,
                    chapterToUse,
                    cancellationToken
                )
            )
            {
                return true; // Duplicate was handled (orphaned file deleted)
            }

            // Find or create the manga series for this chapter
            var manga = await chapterProcessingService.GetOrCreateMangaSeriesAsync(
                library,
                chapterToUse.Metadata.Series,
                null,
                cancellationToken
            );

            // Move original file to correct location if needed
            string finalOriginalPath = MoveOrphanedOriginalFileToCorrectLocation(
                library,
                group,
                manga,
                cancellationToken
            );

            // Handle upscaled file movement if required
            if (
                group.RequiresFileMovement
                && group.UpscaledChapter != null
                && !string.IsNullOrEmpty(library.UpscaledLibraryPath)
            )
            {
                chapterProcessingService.MoveUpscaledFileToLibrary(
                    group.SourcePath!,
                    library,
                    group.CanonicalRelativePath,
                    cancellationToken
                );
            }

            // Fix metadata in the moved files
            await FixOrphanedFileMetadata(
                library,
                group,
                manga,
                finalOriginalPath,
                cancellationToken
            );

            // Create the chapter entity - use original chapter info if available, otherwise upscaled
            var chapter = new Chapter
            {
                MangaId = manga.Id,
                Manga = manga,
                FileName = chapterToUse.FileName,
                RelativePath = group.CanonicalRelativePath,
                IsUpscaled = group.UpscaledChapter != null, // Set to true if we have an upscaled variant
            };

            // Set upscaler profile if we extracted one from the upscaled file
            if (group.UpscalerProfileDto != null)
            {
                // Find or create the upscaler profile
                var upscalerProfile =
                    await chapterProcessingService.FindOrCreateUpscalerProfileAsync(
                        group.UpscalerProfileDto,
                        cancellationToken
                    );
                if (upscalerProfile != null)
                {
                    chapter.UpscalerProfileId = upscalerProfile.Id;
                }
            }

            dbContext.Chapters.Add(chapter);
            await dbContext.SaveChangesAsync(cancellationToken);

            string variants =
                group.OriginalChapter != null && group.UpscaledChapter != null
                    ? "original and upscaled"
                : group.OriginalChapter != null ? "original only"
                : "upscaled only";

            logger.LogInformation(
                "Created chapter entity for orphaned files ({Variants}) '{FileName}' in series '{SeriesTitle}' (Library: '{LibraryName}')",
                variants,
                chapterToUse.FileName,
                chapterToUse.Metadata.Series,
                library.Name
            );

            return true;
        }
        catch (Exception ex)
        {
            var chapterToUse = group.OriginalChapter ?? group.UpscaledChapter;
            logger.LogError(
                ex,
                "Failed to create chapter entity for orphaned chapter group with canonical path '{CanonicalPath}'",
                group.CanonicalRelativePath
            );
            return false;
        }
    }

    /// <summary>
    /// Handles the case where we found an upscaled orphan file and there's already an existing original chapter.
    /// </summary>
    private async Task<bool> HandleUpscaledFileForExistingChapter(
        Library library,
        ChapterGroup group,
        Chapter existingChapter,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Handle file movement if required
            if (
                group.RequiresFileMovement
                && group.UpscaledChapter != null
                && !string.IsNullOrEmpty(library.UpscaledLibraryPath)
            )
            {
                chapterProcessingService.MoveUpscaledFileToLibrary(
                    group.SourcePath!,
                    library,
                    group.CanonicalRelativePath,
                    cancellationToken
                );
            }

            // Update the existing chapter to mark it as upscaled
            existingChapter.IsUpscaled = true;

            // Set upscaler profile if we extracted one from the upscaled file
            if (group.UpscalerProfileDto != null)
            {
                var upscalerProfile =
                    await chapterProcessingService.FindOrCreateUpscalerProfileAsync(
                        group.UpscalerProfileDto,
                        cancellationToken
                    );
                if (upscalerProfile != null)
                {
                    existingChapter.UpscalerProfileId = upscalerProfile.Id;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Updated existing chapter entity '{FileName}' to mark as upscaled (Library: '{LibraryName}')",
                existingChapter.FileName,
                library.Name
            );

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to update existing chapter entity for upscaled file with canonical path '{CanonicalPath}'",
                group.CanonicalRelativePath
            );
            return false;
        }
    }

    /// <summary>
    /// Represents a group of chapter files (original and/or upscaled variants) that belong to the same logical chapter.
    /// </summary>
    private class ChapterGroup
    {
        public required string CanonicalRelativePath { get; set; }
        public FoundChapter? OriginalChapter { get; set; }
        public FoundChapter? UpscaledChapter { get; set; }
        public UpscalerProfileJsonDto? UpscalerProfileDto { get; set; }
        public bool RequiresFileMovement { get; set; }
        public string? SourcePath { get; set; }
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
