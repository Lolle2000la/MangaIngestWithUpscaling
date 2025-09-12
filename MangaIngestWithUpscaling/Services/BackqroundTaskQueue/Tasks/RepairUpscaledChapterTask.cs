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

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class RepairUpscaledChapterTask : BaseTask
{
    public RepairUpscaledChapterTask() { }

    public RepairUpscaledChapterTask(Chapter chapter, List<string> missingPages, List<string> extraPages)
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
        MissingPages = missingPages;
        ExtraPages = extraPages;
        FriendlyEntryName =
            $"Repairing {chapter.FileName} of {chapter.Manga.PrimaryTitle} ({missingPages.Count} missing, {extraPages.Count} extra pages)";
    }

    public override string TaskFriendlyName => FriendlyEntryName;

    public int UpscalerProfileId { get; set; }
    public int ChapterId { get; set; }
    public List<string> MissingPages { get; set; } = new();
    public List<string> ExtraPages { get; set; } = new();
    public string FriendlyEntryName { get; set; } = string.Empty;

    public override int RetryFor { get; set; } = 1;

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<RepairUpscaledChapterTask>>();
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
        UpscalerProfile? upscalerProfile = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
            c => c.Id == UpscalerProfileId, cancellationToken);

        if (chapter == null || upscalerProfile == null)
        {
            throw new InvalidOperationException(
                $"Chapter ({chapter?.RelativePath ?? "Not found"}) or upscaler profile ({upscalerProfile?.Name ?? "Not found"}, id: {UpscalerProfileId}) not found.");
        }

        if (chapter.Manga?.Library?.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException(
                $"Upscaled library path of library {chapter.Manga?.Library?.Name ?? "Unknown"} ({chapter.Manga?.Library?.Id}) not set.");
        }

        string upscaleTargetPath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, chapter.RelativePath);
        string currentStoragePath = Path.Combine(chapter.Manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);

        logger.LogInformation("Starting repair of upscaled chapter {ChapterFileName} - {MissingCount} missing pages, {ExtraCount} extra pages",
            chapter.FileName, MissingPages.Count, ExtraPages.Count);

        try
        {
            // First, remove extra pages from the upscaled CBZ
            if (ExtraPages.Count > 0)
            {
                RemoveExtraPages(upscaleTargetPath, ExtraPages, logger);
            }

            // Then, upscale missing pages and add them to the CBZ
            if (MissingPages.Count > 0)
            {
                await UpscaleMissingPages(currentStoragePath, upscaleTargetPath, upscalerProfile, MissingPages, services, cancellationToken);
            }

            logger.LogInformation("Successfully repaired upscaled chapter {ChapterFileName}", chapter.FileName);
            _ = chapterChangedNotifier.Notify(chapter, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to repair upscaled chapter {ChapterFileName}", chapter.FileName);
            throw;
        }
    }

    private void RemoveExtraPages(string upscaledPath, List<string> extraPages, ILogger logger)
    {
        foreach (string pageName in extraPages)
        {
            bool removed = CbzCleanupHelpers.TryRemoveImageByBaseName(upscaledPath, pageName, logger);
            if (!removed)
            {
                logger.LogWarning("Failed to remove extra page {PageName} from {UpscaledPath}", pageName, upscaledPath);
            }
        }
    }

    private async Task UpscaleMissingPages(string originalPath, string upscaledPath, UpscalerProfile upscalerProfile, 
        List<string> missingPages, IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<RepairUpscaledChapterTask>>();
        var upscaler = services.GetRequiredService<IUpscaler>();

        // Create a temporary directory and CBZ file for the missing pages
        string tempDir = Path.Combine(Path.GetTempPath(), $"repair_{Guid.NewGuid()}");
        string tempMissingPagesCbz = Path.Combine(tempDir, "missing_pages.cbz");
        string tempUpscaledCbz = Path.Combine(tempDir, "upscaled_missing_pages.cbz");
        
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create a temporary CBZ with only the missing pages
            CreateMissingPagesCbz(originalPath, tempMissingPagesCbz, missingPages, logger);

            // Upscale the temporary CBZ
            await upscaler.Upscale(tempMissingPagesCbz, tempUpscaledCbz, upscalerProfile, cancellationToken);

            // Merge the upscaled pages back into the main upscaled CBZ
            MergeUpscaledPages(upscaledPath, tempUpscaledCbz, logger);
        }
        finally
        {
            // Clean up temp directory
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up temporary directory {TempDir}", tempDir);
                }
            }
        }
    }

    private void CreateMissingPagesCbz(string originalPath, string outputCbzPath, List<string> missingPages, ILogger logger)
    {
        using var originalArchive = ZipFile.OpenRead(originalPath);
        using var outputArchive = new FileStream(outputCbzPath, FileMode.Create);
        using var zipArchive = new ZipArchive(outputArchive, ZipArchiveMode.Create);
        
        foreach (var entry in originalArchive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string baseNameWithoutExt = Path.GetFileNameWithoutExtension(entry.FullName);
            if (missingPages.Contains(baseNameWithoutExt))
            {
                var newEntry = zipArchive.CreateEntry(entry.FullName);
                using var originalStream = entry.Open();
                using var newStream = newEntry.Open();
                originalStream.CopyTo(newStream);
                logger.LogDebug("Added missing page {PageName} to temporary CBZ", baseNameWithoutExt);
            }
        }
    }

    private void MergeUpscaledPages(string targetCbzPath, string sourceCbzPath, ILogger logger)
    {
        using var targetArchive = ZipFile.Open(targetCbzPath, ZipArchiveMode.Update);
        using var sourceArchive = ZipFile.OpenRead(sourceCbzPath);
        
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            if (string.IsNullOrEmpty(sourceEntry.Name)) continue;

            // Check if this is an image file
            string extension = Path.GetExtension(sourceEntry.FullName).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".bmp")) continue;
            
            // Remove existing entry if it exists (shouldn't happen for missing pages, but just in case)
            var existingEntry = targetArchive.GetEntry(sourceEntry.FullName);
            existingEntry?.Delete();
            
            // Add the new upscaled page
            var newEntry = targetArchive.CreateEntry(sourceEntry.FullName);
            using var sourceStream = sourceEntry.Open();
            using var targetStream = newEntry.Open();
            sourceStream.CopyTo(targetStream);
            logger.LogDebug("Added upscaled page {EntryName} to target CBZ", sourceEntry.FullName);
        }
    }
}