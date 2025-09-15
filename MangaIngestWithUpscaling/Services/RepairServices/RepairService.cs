using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using System.IO.Compression;

namespace MangaIngestWithUpscaling.Services.RepairServices;

/// <summary>
/// Service for handling chapter repair operations, including preparation and merging of upscaled content.
/// </summary>
public class RepairService : IRepairService
{
    /// <summary>
    /// Prepares a repair context by analyzing differences, extracting pages, and creating temporary CBZ files.
    /// </summary>
    public RepairContext PrepareRepairContext(
        PageDifferenceResult differences,
        string originalPath,
        string upscaledPath,
        ILogger logger)
    {
        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"manga_repair_{Guid.NewGuid()}");
        string tempOriginalDir = Path.Combine(tempWorkDir, "original");
        string tempUpscaledDir = Path.Combine(tempWorkDir, "upscaled");
        string tempMissingDir = Path.Combine(tempWorkDir, "missing");
        string tempMissingCbz = Path.Combine(tempWorkDir, "missing_pages.cbz");
        string tempUpscaledMissingCbz = Path.Combine(tempWorkDir, "upscaled_missing_pages.cbz");

        Directory.CreateDirectory(tempWorkDir);
        Directory.CreateDirectory(tempOriginalDir);
        Directory.CreateDirectory(tempUpscaledDir);
        Directory.CreateDirectory(tempMissingDir);

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

        // Extract missing pages to temporary directory
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

        // Create CBZ with missing pages (only if there are missing pages)
        if (differences.MissingPages.Count > 0)
        {
            ZipFile.CreateFromDirectory(tempMissingDir, tempMissingCbz);
        }

        return new RepairContext
        {
            WorkDirectory = tempWorkDir,
            UpscaledDirectory = tempUpscaledDir,
            MissingPagesCbz = tempMissingCbz,
            UpscaledMissingCbz = tempUpscaledMissingCbz,
            HasMissingPages = differences.MissingPages.Count > 0
        };
    }

    /// <summary>
    /// Merges upscaled missing pages back into the upscaled directory and creates the final CBZ.
    /// </summary>
    public void MergeRepairResults(RepairContext context, string finalUpscaledPath, ILogger logger)
    {
        if (context.HasMissingPages)
        {
            // Extract upscaled missing pages
            string tempUpscaledMissingDir = Path.Combine(context.WorkDirectory, "upscaled_missing");
            Directory.CreateDirectory(tempUpscaledMissingDir);

            // If directory already populated (e.g., local flow already processed results), skip re-extracting
            bool alreadyPopulated = Directory.EnumerateFileSystemEntries(tempUpscaledMissingDir).Any();
            if (!alreadyPopulated)
            {
                // Overwrite any existing files to be safe
                ZipFile.ExtractToDirectory(context.UpscaledMissingCbz, tempUpscaledMissingDir, true);
            }

            // Copy upscaled missing pages back to the upscaled directory
            foreach (var upscaledFile in Directory.GetFiles(tempUpscaledMissingDir))
            {
                var destFile = Path.Combine(context.UpscaledDirectory, Path.GetFileName(upscaledFile));
                File.Copy(upscaledFile, destFile, true);
                logger.LogDebug("Added repaired page: {fileName}", Path.GetFileName(upscaledFile));
            }
        }

        // Create new CBZ file from repaired directory
        string tempRepairedCbz = Path.Combine(context.WorkDirectory, "repaired.cbz");
        ZipFile.CreateFromDirectory(context.UpscaledDirectory, tempRepairedCbz);

        // Replace the original upscaled file
        File.Delete(finalUpscaledPath);
        File.Move(tempRepairedCbz, finalUpscaledPath);
    }

    /// <summary>
    /// Creates a CBZ file containing all missing pages for batch upscaling.
    /// </summary>
    public async Task<string> CreateMissingPagesBatch(
        string inputDir,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Get all image files in the input directory
        var imageFiles = Directory.GetFiles(inputDir)
            .Where(f => ImageConstants.IsSupportedImageExtension(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // stable order
            .ToList();

        if (imageFiles.Count == 0)
        {
            logger.LogDebug("No image files found to upscale in {inputDir}", inputDir);
            return string.Empty;
        }

        string tempBatchInputCbz = Path.Combine(Path.GetTempPath(), $"temp_missing_batch_{Guid.NewGuid()}.cbz");

        // Package all missing images into a single CBZ (entry names are the original basenames)
        using (ZipArchive archive = ZipFile.Open(tempBatchInputCbz, ZipArchiveMode.Create))
        {
            foreach (string file in imageFiles)
            {
                string entryName = Path.GetFileName(file);
                archive.CreateEntryFromFile(file, entryName);
            }
        }

        await Task.CompletedTask; // Make this truly async if needed in the future
        return tempBatchInputCbz;
    }

    /// <summary>
    /// Processes batch upscaled results and extracts them to the output directory.
    /// </summary>
    public async Task ProcessBatchUpscaleResults(
        string batchOutputCbz,
        string outputDir,
        int expectedPageCount,
        IProgress<UpscaleProgress> progress,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);

        // Extract all upscaled images to the output directory
        int extracted = 0;
        using (ZipArchive outArchive = ZipFile.OpenRead(batchOutputCbz))
        {
            foreach (ZipArchiveEntry entry in outArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // filter only known image extensions
                if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.Name).ToLowerInvariant()))
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
                    expectedPageCount,
                    Math.Min(extracted, expectedPageCount),
                    "Upscaling missing pages (batch)",
                    $"Processed {Math.Min(extracted, expectedPageCount)}/{expectedPageCount} pages"
                ));
            }
        }

        logger.LogDebug("Batch upscaled {extracted}/{total} missing pages", extracted, expectedPageCount);
        await Task.CompletedTask; // Make this truly async if needed in the future
    }
}