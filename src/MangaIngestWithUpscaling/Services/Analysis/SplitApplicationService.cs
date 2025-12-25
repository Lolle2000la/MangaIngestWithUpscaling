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
using NetVips;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitApplicationService(
    ApplicationDbContext dbContext,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    ISplitApplier splitApplier,
    ILogger<SplitApplicationService> logger
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
            throw new InvalidOperationException($"Chapter {chapterId} not found.");
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
                $"Original chapter file not found at {originalCbzPath}"
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

            var splitPagesMap = new Dictionary<string, List<string>>(); // OriginalFileName -> List<NewFilePaths>

            foreach (var imagePath in originalImages)
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
                var finding = findings.FirstOrDefault(f => f.PageFileName == fileNameWithoutExt);

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

                foreach (var imagePath in upscaledImages)
                {
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);

                    if (splitPagesMap.ContainsKey(fileNameWithoutExt))
                    {
                        // This page was split in original. We need to split the upscaled page too.
                        // We can calculate the relative split positions.

                        var originalFinding = findings.FirstOrDefault(f =>
                            f.PageFileName == fileNameWithoutExt
                        );
                        if (originalFinding != null)
                        {
                            var result = JsonSerializer.Deserialize<SplitDetectionResult>(
                                originalFinding.SplitJson
                            );
                            if (result != null && result.Splits.Count > 0)
                            {
                                // We need to map the split points from original dimensions to upscaled dimensions
                                // But wait, ApplySplitsToImage takes absolute Y coordinates.
                                // We need to know the scaling factor.

                                using var upscaledImage = Image.NewFromFile(imagePath);
                                double scaleY =
                                    (double)upscaledImage.Height / result.OriginalHeight;

                                var upscaledSplits = result
                                    .Splits.Select(s => new DetectedSplit
                                    {
                                        YOriginal = (int)(s.YOriginal * scaleY),
                                        Confidence = s.Confidence,
                                    })
                                    .ToList();

                                splitApplier.ApplySplitsToImage(
                                    imagePath,
                                    upscaledSplits,
                                    newUpscaledDir
                                );
                                continue;
                            }
                        }
                    }

                    // Copy unsplit image
                    var destPath = Path.Combine(newUpscaledDir, Path.GetFileName(imagePath));
                    File.Copy(imagePath, destPath);
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
