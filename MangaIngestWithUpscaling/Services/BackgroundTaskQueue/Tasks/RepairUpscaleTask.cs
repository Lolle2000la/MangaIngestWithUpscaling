using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Helpers;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

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
        try
        {
            await PerformRepair(upscaler, currentStoragePath, upscaleTargetPath, upscalerProfile, differences, logger,
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
        string originalPath,
        string upscaledPath,
        UpscalerProfile profile,
        PageDifferenceResult differences,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Create temporary directories for extraction and work
        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"manga_repair_{Guid.NewGuid()}");
        string tempOriginalDir = Path.Combine(tempWorkDir, "original");
        string tempUpscaledDir = Path.Combine(tempWorkDir, "upscaled");
        string tempMissingDir = Path.Combine(tempWorkDir, "missing");
        string tempUpscaledMissingDir = Path.Combine(tempWorkDir, "upscaled_missing");

        try
        {
            Directory.CreateDirectory(tempWorkDir);
            Directory.CreateDirectory(tempOriginalDir);
            Directory.CreateDirectory(tempUpscaledDir);
            Directory.CreateDirectory(tempMissingDir);
            Directory.CreateDirectory(tempUpscaledMissingDir);

            // Extract both archives
            ZipFile.ExtractToDirectory(originalPath, tempOriginalDir);
            ZipFile.ExtractToDirectory(upscaledPath, tempUpscaledDir);

            // Remove extra pages from upscaled version
            foreach (var extraPage in differences.ExtraPages)
            {
                var extraFiles = Directory.GetFiles(tempUpscaledDir, $"{extraPage}.*", SearchOption.AllDirectories);
                foreach (var file in extraFiles)
                {
                    File.Delete(file);
                    logger.LogDebug("Removed extra page: {fileName}", Path.GetFileName(file));
                }
            }

            // Extract missing pages to temporary directory for upscaling
            foreach (var missingPage in differences.MissingPages)
            {
                var missingFiles = Directory.GetFiles(tempOriginalDir, $"{missingPage}.*", SearchOption.AllDirectories);
                foreach (var file in missingFiles)
                {
                    var destFile = Path.Combine(tempMissingDir, Path.GetFileName(file));
                    File.Copy(file, destFile);
                    logger.LogDebug("Extracted missing page for upscaling: {fileName}", Path.GetFileName(file));
                }
            }

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

                // Use folder-based upscaling for the missing pages
                await UpscaleFolderContents(upscaler, tempMissingDir, tempUpscaledMissingDir, profile, reporter, logger,
                    cancellationToken);

                // Copy upscaled missing pages back to the upscaled directory
                foreach (var upscaledFile in Directory.GetFiles(tempUpscaledMissingDir))
                {
                    var destFile = Path.Combine(tempUpscaledDir, Path.GetFileName(upscaledFile));
                    File.Copy(upscaledFile, destFile);
                    logger.LogDebug("Added repaired page: {fileName}", Path.GetFileName(upscaledFile));
                }
            }

            // Update progress for final packaging
            Progress.StatusMessage = "Packaging repaired CBZ";

            // Create new CBZ file from repaired directory
            string tempRepairedCbz = Path.Combine(tempWorkDir, "repaired.cbz");
            ZipFile.CreateFromDirectory(tempUpscaledDir, tempRepairedCbz);

            // Replace the original upscaled file
            File.Delete(upscaledPath);
            File.Move(tempRepairedCbz, upscaledPath);

            logger.LogInformation(
                "Successfully repaired chapter with {missingCount} missing pages and {extraCount} extra pages removed",
                differences.MissingPages.Count, differences.ExtraPages.Count);
        }
        finally
        {
            // Clean up temporary directories
            if (Directory.Exists(tempWorkDir))
            {
                Directory.Delete(tempWorkDir, true);
            }
        }
    }

    private async Task UpscaleFolderContents(
        IUpscaler upscaler,
        string inputDir,
        string outputDir,
        UpscalerProfile profile,
        IProgress<UpscaleProgress> progress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Get all image files in the input directory
        var imageFiles = Directory.GetFiles(inputDir)
            .Where(f => f.ToLowerInvariant().EndsWithAny(".png", ".jpg", ".jpeg", ".avif", ".webp", ".bmp"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // stable order
            .ToList();

        if (imageFiles.Count == 0)
        {
            logger.LogDebug("No image files found to upscale in {inputDir}", inputDir);
            return;
        }

        Directory.CreateDirectory(outputDir);

        // Create a temporary CBZ containing all missing pages and one output CBZ
        string tempBatchInputCbz = Path.Combine(Path.GetTempPath(), $"temp_missing_batch_{Guid.NewGuid()}.cbz");
        string tempBatchOutputCbz = Path.Combine(Path.GetTempPath(), $"temp_missing_batch_out_{Guid.NewGuid()}.cbz");

        try
        {
            // Package all missing images into a single CBZ (entry names are the original basenames)
            using (ZipArchive archive = ZipFile.Open(tempBatchInputCbz, ZipArchiveMode.Create))
            {
                foreach (string file in imageFiles)
                {
                    string entryName = Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, entryName);
                }
            }

            // Basic progress info before starting batch upscale
            progress.Report(new UpscaleProgress(
                imageFiles.Count,
                0,
                "Upscaling missing pages (batch)",
                $"Submitting {imageFiles.Count} pages to upscaler"
            ));

            // Run the upscaler once for the batch
            await upscaler.Upscale(tempBatchInputCbz, tempBatchOutputCbz, profile, cancellationToken);

            // Extract all upscaled images to the output directory
            int extracted = 0;
            using (ZipArchive outArchive = ZipFile.OpenRead(tempBatchOutputCbz))
            {
                foreach (ZipArchiveEntry entry in outArchive.Entries)
                {
                    // filter only known image extensions
                    if (!entry.FullName.ToLowerInvariant()
                            .EndsWithAny(".png", ".jpg", ".jpeg", ".avif", ".webp", ".bmp"))
                    {
                        continue;
                    }

                    // Sanitize entry name and ensure no directory traversal
                    string safeName = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrWhiteSpace(safeName))
                    {
                        continue;
                    }

                    string destPath = Path.Combine(outputDir, safeName);

                    // Ensure destination overwrite if exists
                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }

                    entry.ExtractToFile(destPath);
                    extracted++;

                    // Report coarse-grained progress while extracting results
                    progress.Report(new UpscaleProgress(
                        imageFiles.Count,
                        Math.Min(extracted, imageFiles.Count),
                        "Upscaling missing pages (batch)",
                        $"Processed {Math.Min(extracted, imageFiles.Count)}/{imageFiles.Count} pages"
                    ));
                }
            }

            logger.LogDebug("Batch upscaled {extracted}/{total} missing pages", extracted, imageFiles.Count);
        }
        finally
        {
            // Clean up temporary files
            if (File.Exists(tempBatchInputCbz))
            {
                File.Delete(tempBatchInputCbz);
            }

            if (File.Exists(tempBatchOutputCbz))
            {
                File.Delete(tempBatchOutputCbz);
            }
        }
    }
}