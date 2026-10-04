using System.IO.Compression;
using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitApplicationService(
    ApplicationDbContext dbContext,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    ISplitApplier splitApplier,
    IUpscaler upscaler,
    ITaskQueue taskQueue,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<SplitApplicationService> logger,
    IStringLocalizer<SplitApplicationService> localizer
) : ISplitApplicationService
{
    public async Task ApplySplitsAsync(
        int chapterId,
        int detectorVersion,
        CancellationToken cancellationToken
    )
    {
        var chapter = await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
                    .ThenInclude(l => l.UpscalerProfile)
            .Include(c => c.Manga)
                .ThenInclude(m => m.UpscalerProfilePreference)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == chapterId, cancellationToken);

        if (chapter == null)
        {
            throw new InvalidOperationException(localizer["Error_ChapterNotFound", chapterId]);
        }

        var findings = await dbContext
            .StripSplitFindings.Where(f =>
                f.ChapterId == chapterId && f.DetectorVersion == detectorVersion
            )
            .ToListAsync(cancellationToken);

        if (findings.Count == 0)
        {
            logger.LogInformation(
                "No splits found for chapter {ChapterId} (version {Version}), nothing to apply.",
                chapterId,
                detectorVersion
            );
            await splitProcessingCoordinator.OnSplitsAppliedAsync(
                chapterId,
                detectorVersion,
                cancellationToken
            );
            return;
        }

        var libraryPath = chapter.Manga.Library.NotUpscaledLibraryPath;
        var originalCbzPath = Path.Combine(libraryPath, chapter.RelativePath);

        if (!File.Exists(originalCbzPath))
        {
            throw new FileNotFoundException(
                localizer["Error_OriginalChapterFileNotFound", originalCbzPath]
            );
        }

        // Create temp directories
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "mangaingest_split_apply",
            Guid.NewGuid().ToString()
        );
        var originalExtractDir = Path.Combine(tempRoot, "original");
        var upscaledExtractDir = Path.Combine(tempRoot, "upscaled");
        var newOriginalDir = Path.Combine(tempRoot, "new_original");
        var newUpscaledDir = Path.Combine(tempRoot, "new_upscaled");

        Directory.CreateDirectory(originalExtractDir);
        Directory.CreateDirectory(newOriginalDir);

        // Each replacement is built next to the file it replaces and moved onto it, so the swap is a
        // rename instead of a copy that rewrites the chapter in place (see TempSiblingPathFor). Both
        // are removed below when the apply never reaches its move.
        string tempOriginalCbz = FileSystemHelpers.TempSiblingPathFor(originalCbzPath, "splits");
        string? tempUpscaledCbz = null;

        try
        {
            // 1. Process Original
            logger.LogInformation("Applying splits to original chapter {ChapterId}", chapterId);
            cancellationToken.ThrowIfCancellationRequested();
            ZipFile.ExtractToDirectory(originalCbzPath, originalExtractDir);

            var originalImages = Directory
                .GetFiles(originalExtractDir)
                .Where(f => ImageConstants.SupportedImageExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            var splitPagesMap = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase
            ); // OriginalFileName -> List<NewFilePaths>

            foreach (var imagePath in originalImages)
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
                var finding = findings.FirstOrDefault(f =>
                    f.PageFileName.Equals(fileNameWithoutExt, StringComparison.OrdinalIgnoreCase)
                );

                if (finding != null)
                {
                    var result = JsonSerializer.Deserialize<SplitDetectionResult>(
                        finding.SplitJson
                    );
                    if (result != null && result.Splits.Count > 0)
                    {
                        var newParts = splitApplier.ApplySplitsToImage(
                            imagePath,
                            result.Splits,
                            newOriginalDir
                        );
                        splitPagesMap[fileNameWithoutExt] = newParts;
                        continue;
                    }
                }

                // Copy unsplit image
                var destPath = Path.Combine(newOriginalDir, Path.GetFileName(imagePath));
                File.Copy(imagePath, destPath);
            }

            // Update ComicInfo in newOriginalDir
            await UpdateComicInfoAsync(originalExtractDir, newOriginalDir);

            // Repack Original
            FileSystemHelpers.DeleteStaleTempSiblings(
                Path.GetDirectoryName(originalCbzPath)!,
                logger
            );
            ZipFile.CreateFromDirectory(newOriginalDir, tempOriginalCbz);

            // The original is swapped in only after the upscaled rebuild below succeeds: moving it
            // first left the chapter with split pages but a stale upscaled CBZ when the rebuild threw,
            // and a retry could no longer match the findings (the original page is already split), so
            // it silently marked the stale upscaled chapter as applied. Both swaps happen together at
            // the end.

            // 2. Process Upscaled if exists. Resolve the effective profile: a chapter can be upscaled
            // with an inherited library/manga profile and no explicit FK (e.g. LibraryIntegrityChecker
            // sets IsUpscaled without one), so the explicit FK alone would skip the rebuild and leave
            // a stale upscaled CBZ in RemoteOnly.
            var effectiveProfile =
                chapter.UpscalerProfile ?? chapter.Manga?.EffectiveUpscalerProfile;
            bool originalSwapped = false;
            if (
                chapter.IsUpscaled
                && chapter.UpscaledFullPath != null
                && File.Exists(chapter.UpscaledFullPath)
                && effectiveProfile != null
            )
            {
                if (upscalerConfig.Value.RemoteOnly)
                {
                    // A remote-only server has no local ML backend, so it cannot upscale the new
                    // split pages inline. Enqueue a repair task: the worker upscales only the pages
                    // that differ (the new split pages) and merges them into the existing upscaled
                    // CBZ. A plain UpscaleTask would be skipped because the chapter is already
                    // upscaled, leaving the stale upscaled CBZ in place.
                    //
                    // Swap the original first so the enqueued repair reads the split chapter rather
                    // than the pre-split one.
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tempOriginalCbz, originalCbzPath, true);
                    originalSwapped = true;

                    // OnSplitsAppliedAsync (below) enqueues a repair of its own when the library
                    // upscales on ingest; only enqueue one here when it will not, so the upscaled CBZ
                    // is never left stale (the inline path always updates it).
                    bool repairedOnApplied =
                        chapter.Manga?.Library?.UpscaleOnIngest == true
                        && chapter.Manga.ShouldUpscale != false
                        && chapter.Manga.Library.UpscalerProfileId != null;
                    if (!repairedOnApplied)
                    {
                        logger.LogInformation(
                            "Remote-only mode: deferring the split-chapter repair for {ChapterId} to a worker.",
                            chapterId
                        );
                        await taskQueue.EnqueueAsync(
                            new RepairUpscaleTask(chapter, effectiveProfile)
                        );
                    }
                }
                else
                {
                    logger.LogInformation(
                        "Applying splits to upscaled chapter {ChapterId}",
                        chapterId
                    );
                    Directory.CreateDirectory(upscaledExtractDir);
                    Directory.CreateDirectory(newUpscaledDir);

                    cancellationToken.ThrowIfCancellationRequested();
                    ZipFile.ExtractToDirectory(chapter.UpscaledFullPath, upscaledExtractDir);

                    var upscaledImages = Directory
                        .GetFiles(upscaledExtractDir)
                        .Where(f =>
                            ImageConstants.SupportedImageExtensions.Contains(Path.GetExtension(f))
                        )
                        .ToList();

                    // Collect pages that need to be upscaled (split pages from original)
                    var splitPagesToUpscale = new Dictionary<string, List<string>>(
                        StringComparer.OrdinalIgnoreCase
                    ); // old page name -> new split page paths

                    foreach (var imagePath in upscaledImages)
                    {
                        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);

                        if (splitPagesMap.TryGetValue(fileNameWithoutExt, out var splitPages))
                        {
                            // This page was split in the original. Instead of trying to split the upscaled
                            // version with coordinate scaling (which doesn't work correctly with
                            // MaxDimensionBeforeUpscaling), we'll upscale the new split pages from the original.
                            splitPagesToUpscale[fileNameWithoutExt] = splitPages;
                            // Don't copy this old upscaled page - it will be replaced by split upscaled pages
                        }
                        else
                        {
                            // Copy unsplit image
                            var destPath = Path.Combine(
                                newUpscaledDir,
                                Path.GetFileName(imagePath)
                            );
                            File.Copy(imagePath, destPath);
                        }
                    }

                    // Upscale the split pages if there are any
                    if (splitPagesToUpscale.Count > 0)
                    {
                        logger.LogInformation(
                            "Upscaling {Count} split pages for chapter {ChapterId}",
                            splitPagesToUpscale.Values.Sum(v => v.Count),
                            chapterId
                        );

                        // Create a temporary CBZ with just the split pages
                        var splitPagesCbzDir = Path.Combine(tempRoot, "split_pages_to_upscale");
                        Directory.CreateDirectory(splitPagesCbzDir);

                        foreach (var splitPages in splitPagesToUpscale.Values)
                        {
                            foreach (var splitPagePath in splitPages)
                            {
                                var destPath = Path.Combine(
                                    splitPagesCbzDir,
                                    Path.GetFileName(splitPagePath)
                                );
                                File.Copy(splitPagePath, destPath);
                            }
                        }

                        var splitPagesCbz = Path.Combine(tempRoot, "split_pages.cbz");
                        var upscaledSplitPagesCbz = Path.Combine(
                            tempRoot,
                            "split_pages_upscaled.cbz"
                        );

                        ZipFile.CreateFromDirectory(splitPagesCbzDir, splitPagesCbz);

                        // Upscale the split pages
                        await upscaler.Upscale(
                            splitPagesCbz,
                            upscaledSplitPagesCbz,
                            effectiveProfile,
                            cancellationToken
                        );

                        // Extract upscaled split pages and add them to the new upscaled directory
                        var upscaledSplitPagesDir = Path.Combine(tempRoot, "upscaled_split_pages");
                        Directory.CreateDirectory(upscaledSplitPagesDir);
                        ZipFile.ExtractToDirectory(upscaledSplitPagesCbz, upscaledSplitPagesDir);

                        var upscaledSplitImages = Directory
                            .GetFiles(upscaledSplitPagesDir)
                            .Where(f =>
                                ImageConstants.SupportedImageExtensions.Contains(
                                    Path.GetExtension(f)
                                )
                            )
                            .ToList();

                        foreach (var upscaledSplitImage in upscaledSplitImages)
                        {
                            var destPath = Path.Combine(
                                newUpscaledDir,
                                Path.GetFileName(upscaledSplitImage)
                            );
                            File.Copy(upscaledSplitImage, destPath);
                        }
                    }

                    // Update ComicInfo in newUpscaledDir
                    await UpdateComicInfoAsync(upscaledExtractDir, newUpscaledDir);

                    // Repack Upscaled
                    tempUpscaledCbz = FileSystemHelpers.TempSiblingPathFor(
                        chapter.UpscaledFullPath,
                        "splits"
                    );
                    FileSystemHelpers.DeleteStaleTempSiblings(
                        Path.GetDirectoryName(chapter.UpscaledFullPath)!,
                        logger
                    );
                    ZipFile.CreateFromDirectory(newUpscaledDir, tempUpscaledCbz);

                    // The upscaled swap happens with the original swap below.
                }
            }

            // Swap both replacements in last, so a failure while building either one (in particular
            // the upscaled rebuild) leaves the chapter untouched and the apply is safely retryable.
            // Observe cancellation before each swap: a cancelled apply must not move its replacement
            // back over a chapter a concurrent merge has already deleted.
            if (!originalSwapped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempOriginalCbz, originalCbzPath, true);
            }
            if (tempUpscaledCbz is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempUpscaledCbz, chapter.UpscaledFullPath!, true);
            }

            await splitProcessingCoordinator.OnSplitsAppliedAsync(
                chapterId,
                detectorVersion,
                cancellationToken
            );
        }
        finally
        {
            DeleteUnusedReplacement(tempOriginalCbz);
            if (tempUpscaledCbz is not null)
            {
                DeleteUnusedReplacement(tempUpscaledCbz);
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    ///     Removes a replacement the apply built but never moved onto its destination, so a failed run
    ///     does not leave a full-size temp in the library.
    /// </summary>
    private void DeleteUnusedReplacement(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to delete the unused split replacement {Temp}.",
                tempPath
            );
        }
    }

    private async Task UpdateComicInfoAsync(string sourceDir, string destDir)
    {
        // Try to find ComicInfo.xml in source
        var sourceXml = Path.Combine(sourceDir, "ComicInfo.xml");
        if (File.Exists(sourceXml))
        {
            var destXml = Path.Combine(destDir, "ComicInfo.xml");
            File.Copy(sourceXml, destXml, true);

            // We could update metadata here if needed.
            // Since the file now exists, WriteComicInfoAsync would work if we wanted to ensure consistency.
            // For now, simply copying preserves the original metadata including fields we don't track.
        }
    }
}
