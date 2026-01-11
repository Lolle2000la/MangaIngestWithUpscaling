using System.IO.Compression;
using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitApplicationService(
    ApplicationDbContext dbContext,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    ISplitApplier splitApplier,
    IUpscaler upscaler,
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

        try
        {
            // 1. Process Original
            logger.LogInformation("Applying splits to original chapter {ChapterId}", chapterId);
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
            var tempOriginalCbz = Path.Combine(tempRoot, "original.cbz");
            ZipFile.CreateFromDirectory(newOriginalDir, tempOriginalCbz);

            // Replace Original
            File.Move(tempOriginalCbz, originalCbzPath, true);

            // 2. Process Upscaled if exists
            if (
                chapter.IsUpscaled
                && chapter.UpscaledFullPath != null
                && File.Exists(chapter.UpscaledFullPath)
                && chapter.UpscalerProfile != null
            )
            {
                logger.LogInformation("Applying splits to upscaled chapter {ChapterId}", chapterId);
                Directory.CreateDirectory(upscaledExtractDir);
                Directory.CreateDirectory(newUpscaledDir);

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
                        var destPath = Path.Combine(newUpscaledDir, Path.GetFileName(imagePath));
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
                    var upscaledSplitPagesCbz = Path.Combine(tempRoot, "split_pages_upscaled.cbz");

                    ZipFile.CreateFromDirectory(splitPagesCbzDir, splitPagesCbz);

                    // Upscale the split pages
                    await upscaler.Upscale(
                        splitPagesCbz,
                        upscaledSplitPagesCbz,
                        chapter.UpscalerProfile,
                        cancellationToken
                    );

                    // Extract upscaled split pages and add them to the new upscaled directory
                    var upscaledSplitPagesDir = Path.Combine(tempRoot, "upscaled_split_pages");
                    Directory.CreateDirectory(upscaledSplitPagesDir);
                    ZipFile.ExtractToDirectory(upscaledSplitPagesCbz, upscaledSplitPagesDir);

                    var upscaledSplitImages = Directory
                        .GetFiles(upscaledSplitPagesDir)
                        .Where(f =>
                            ImageConstants.SupportedImageExtensions.Contains(Path.GetExtension(f))
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
                var tempUpscaledCbz = Path.Combine(tempRoot, "upscaled.cbz");
                ZipFile.CreateFromDirectory(newUpscaledDir, tempUpscaledCbz);

                // Replace Upscaled
                File.Move(tempUpscaledCbz, chapter.UpscaledFullPath, true);
            }

            await splitProcessingCoordinator.OnSplitsAppliedAsync(
                chapterId,
                detectorVersion,
                cancellationToken
            );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
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
