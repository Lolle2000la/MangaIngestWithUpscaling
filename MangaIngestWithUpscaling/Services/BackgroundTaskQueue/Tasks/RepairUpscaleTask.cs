using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Task for repairing partially corrupted upscaled chapters by upscaling only missing pages
/// and removing extra pages, then merging them back into the existing CBZ.
/// </summary>
public class RepairUpscaleTask : BaseTask
{
    public RepairUpscaleTask() { }

    public RepairUpscaleTask(Chapter chapter)
    {
        if (chapter.Manga == null)
        {
            throw new InvalidOperationException($"Chapter {chapter.FileName} has no associated manga.");
        }

        if (chapter.Manga.EffectiveUpscalerProfile == null)
        {
            throw new InvalidOperationException(
                $"Chapter {chapter.FileName} of {chapter.Manga?.PrimaryTitle ?? "Unknown"} has no effective upscaler profile set.");
        }

        ChapterId = chapter.Id;
        UpscalerProfileId = chapter.Manga.EffectiveUpscalerProfile.Id;
        FriendlyEntryName =
            $"Repairing upscaled {chapter.FileName} of {chapter.Manga.PrimaryTitle} with {chapter.Manga.EffectiveUpscalerProfile.Name}";
    }

    public RepairUpscaleTask(Chapter chapter, UpscalerProfile profile)
    {
        ChapterId = chapter.Id;
        UpscalerProfileId = profile.Id;
        FriendlyEntryName =
            $"Repairing upscaled {chapter.FileName} of {chapter.Manga.PrimaryTitle} with {profile.Name}";
    }

    public override string TaskFriendlyName => FriendlyEntryName;

    public int UpscalerProfileId { get; set; }
    public int ChapterId { get; set; }

    public string FriendlyEntryName { get; set; } = string.Empty;

    public override int RetryFor { get; set; } = 1;

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<RepairUpscaleTask>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var metadataChanger = services.GetRequiredService<IMangaMetadataChanger>();
        var metadataHandling = services.GetRequiredService<IMetadataHandlingService>();
        var chapterChangedNotifier = services.GetRequiredService<IChapterChangedNotifier>();

        Chapter? chapter = await dbContext.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(
                c => c.Id == ChapterId, cancellationToken);
        UpscalerProfile? upscalerProfile = chapter?.UpscalerProfile ??
                                           await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
                                               c => c.Id == UpscalerProfileId, cancellationToken);

        if (chapter == null || upscalerProfile == null)
        {
            throw new InvalidOperationException(
                $"Chapter ({chapter?.RelativePath ?? "Not found"}) or upscaler profile ({upscalerProfile?.Name ?? "Not found"}, id: {UpscalerProfileId}) not found.");
        }

        if (chapter.UpscaledFullPath == null)
        {
            throw new InvalidOperationException(
                $"Upscaled library path of library {chapter.Manga?.Library?.Name ?? "Unknown"} ({chapter.Manga?.Library?.Id}) not set.");
        }

        string upscaleTargetPath = chapter.UpscaledFullPath;
        string currentStoragePath = chapter.NotUpscaledFullPath;

        logger.LogInformation("Starting repair of chapter \"{chapterFileName}\" of {seriesTitle}",
            chapter.FileName, chapter.Manga.PrimaryTitle);

        // Analyze what pages need repair
        var differences = metadataHandling.AnalyzePageDifferences(currentStoragePath, upscaleTargetPath);

        if (differences.AreEqual)
        {
            logger.LogInformation("Chapter \"{chapterFileName}\" of {seriesTitle} no longer needs repair",
                chapter.FileName, chapter.Manga.PrimaryTitle);
            return;
        }

        if (!differences.CanRepair)
        {
            logger.LogWarning(
                "Chapter \"{chapterFileName}\" of {seriesTitle} cannot be repaired - will fall back to full re-upscale",
                chapter.FileName, chapter.Manga.PrimaryTitle);

            // Fall back to full upscale by creating a regular UpscaleTask
            var fallbackTask = new UpscaleTask(chapter, upscalerProfile);
            await fallbackTask.ProcessAsync(services, cancellationToken);
            return;
        }

        var upscaler = services.GetRequiredService<IUpscaler>();
        var repairService = services.GetRequiredService<IRepairService>();
        try
        {
            await PerformRepair(upscaler, repairService, currentStoragePath, upscaleTargetPath, upscalerProfile,
                differences, logger,
                cancellationToken);
            _ = chapterChangedNotifier.Notify(chapter, true);
        }
        catch (Exception)
        {
            // Clean up on failure - let integrity checker handle it
            if (File.Exists(upscaleTargetPath))
            {
                File.Delete(upscaleTargetPath);
            }

            throw;
        }

        // Reload chapter and manga from db in case title changed
        await dbContext.Entry(chapter).ReloadAsync();
        await dbContext.Entry(chapter.Manga).ReloadAsync();

        // Apply any title changes
        metadataChanger.ApplyMangaTitleToUpscaled(chapter, chapter.Manga.PrimaryTitle, upscaleTargetPath);
    }

    private async Task PerformRepair(
        IUpscaler upscaler,
        IRepairService repairService,
        string originalPath,
        string upscaledPath,
        UpscalerProfile profile,
        PageDifferenceResult differences,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var repairContext = repairService.PrepareRepairContext(differences, originalPath, upscaledPath, logger);

        try
        {
            // Only upscale if there are missing pages
            if (differences.MissingPages.Count > 0)
            {
                // Update progress for missing pages upscaling
                Progress.Total = differences.MissingPages.Count;
                Progress.Current = 0;
                Progress.ProgressUnit = "pages";
                Progress.StatusMessage = "Upscaling missing pages";

                var reporter = new Progress<UpscaleProgress>(p =>
                {
                    if (p.Total.HasValue)
                    {
                        Progress.Total = p.Total.Value;
                    }

                    if (p.Current.HasValue)
                    {
                        Progress.Current = p.Current.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                    {
                        Progress.StatusMessage = p.StatusMessage!;
                    }
                });

                // Use the repair service for batch processing
                await UpscaleFolderContents(upscaler, repairService, repairContext, profile, reporter, logger,
                    cancellationToken);
            }

            // Update progress for final packaging
            Progress.StatusMessage = "Packaging repaired CBZ";

            // Use the repair service to merge results
            repairService.MergeRepairResults(repairContext, upscaledPath, logger);

            logger.LogInformation(
                "Successfully repaired chapter with {missingCount} missing pages and {extraCount} extra pages removed",
                differences.MissingPages.Count, differences.ExtraPages.Count);
        }
        finally
        {
            repairContext.Dispose();
        }
    }

    private async Task UpscaleFolderContents(
        IUpscaler upscaler,
        IRepairService repairService,
        RepairContext repairContext,
        UpscalerProfile profile,
        IProgress<UpscaleProgress> progress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Get all missing pages directory for batch processing
        string tempMissingDir = Path.Combine(repairContext.WorkDirectory, "missing");

        // Basic progress info before starting batch upscale
        progress.Report(new UpscaleProgress(
            null,
            0,
            "Upscaling missing pages (batch)",
            "Preparing batch upscale"
        ));

        // Create batch CBZ using the repair service
        if (repairContext.HasMissingPages)
        {
            // Use the repair service method which handles CBZ creation internally
            await upscaler.Upscale(repairContext.MissingPagesCbz, repairContext.UpscaledMissingCbz, profile,
                cancellationToken);

            // Process results using the repair service
            string tempUpscaledMissingDir = Path.Combine(repairContext.WorkDirectory, "upscaled_missing");
            await repairService.ProcessBatchUpscaleResults(
                repairContext.UpscaledMissingCbz,
                tempUpscaledMissingDir,
                Directory.GetFiles(tempMissingDir).Length,
                progress,
                logger,
                cancellationToken);

            // Copy the extracted files to the upscaled directory
            foreach (var upscaledFile in Directory.GetFiles(tempUpscaledMissingDir))
            {
                var destFile = Path.Combine(repairContext.UpscaledDirectory, Path.GetFileName(upscaledFile));
                File.Copy(upscaledFile, destFile, true);
                logger.LogDebug("Added repaired page: {fileName}", Path.GetFileName(upscaledFile));
            }
        }
    }
}