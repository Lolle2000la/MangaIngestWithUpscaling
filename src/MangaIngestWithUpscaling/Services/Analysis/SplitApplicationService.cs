using System.IO.Compression;
using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace MangaIngestWithUpscaling.Services.Analysis;

[RegisterScoped]
public class SplitApplicationService(
    ApplicationDbContext dbContext,
    IUpscaler upscaler,
    IChapterChangedNotifier chapterChangedNotifier,
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
            await UpdateStateAsync(
                chapterId,
                detectorVersion,
                SplitProcessingStatus.Applied,
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
                        var newParts = ApplySplitsToImage(imagePath, result.Splits, newOriginalDir);
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
                        // This page was split in original. We need to upscale the NEW parts.
                        var newParts = splitPagesMap[fileNameWithoutExt];

                        foreach (var partPath in newParts)
                        {
                            // Upscale partPath -> newUpscaledDir
                            // We need to determine the output filename.
                            // The partPath is in newOriginalDir, e.g. page_001_part1.jpg
                            // We want page_001_part1.png (or whatever format profile uses)

                            // We can use the upscaler to upscale a single file?
                            // IUpscaler.Upscale takes inputPath and outputPath (directories or files?)
                            // Usually it takes directories.
                            // But we can try to upscale single files if the implementation supports it,
                            // or we can put them in a temp dir.

                            // Let's assume we need to upscale individually.
                            // Actually, MangaJaNaiUpscaler.Upscale takes directories.
                            // So we should gather all split parts that need upscaling into a temp dir,
                            // upscale them all at once, and then move them to newUpscaledDir.
                        }
                    }
                    else
                    {
                        // Copy unsplit image
                        var destPath = Path.Combine(newUpscaledDir, Path.GetFileName(imagePath));
                        File.Copy(imagePath, destPath);
                    }
                }

                // Gather all new parts to upscale
                var partsToUpscaleDir = Path.Combine(tempRoot, "parts_to_upscale");
                var partsUpscaledDir = Path.Combine(tempRoot, "parts_upscaled");
                Directory.CreateDirectory(partsToUpscaleDir);
                Directory.CreateDirectory(partsUpscaledDir);

                bool anyToUpscale = false;
                foreach (var kvp in splitPagesMap)
                {
                    foreach (var partPath in kvp.Value)
                    {
                        var dest = Path.Combine(partsToUpscaleDir, Path.GetFileName(partPath));
                        File.Copy(partPath, dest);
                        anyToUpscale = true;
                    }
                }

                if (anyToUpscale)
                {
                    // Upscale
                    var reporter = new Progress<UpscaleProgress>();
                    await upscaler.Upscale(
                        partsToUpscaleDir,
                        partsUpscaledDir,
                        chapter.UpscalerProfile!,
                        reporter,
                        cancellationToken
                    );

                    // Move upscaled parts to newUpscaledDir
                    foreach (var file in Directory.GetFiles(partsUpscaledDir))
                    {
                        var dest = Path.Combine(newUpscaledDir, Path.GetFileName(file));
                        File.Move(file, dest);
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

            await UpdateStateAsync(
                chapterId,
                detectorVersion,
                SplitProcessingStatus.Applied,
                cancellationToken
            );

            // Notify change
            await chapterChangedNotifier.Notify(chapter, chapter.IsUpscaled);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private List<string> ApplySplitsToImage(
        string imagePath,
        List<DetectedSplit> splits,
        string outputDir
    )
    {
        var resultPaths = new List<string>();
        using var image = Image.NewFromFile(imagePath);

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        var ext = Path.GetExtension(imagePath);

        int currentY = 0;
        int partIndex = 1;

        // Sort splits by Y just in case
        var sortedSplits = splits.OrderBy(s => s.YOriginal).ToList();

        foreach (var split in sortedSplits)
        {
            int splitY = split.YOriginal;

            // Validate splitY
            if (splitY <= currentY || splitY >= image.Height)
                continue;

            int height = splitY - currentY;
            using var crop = image.Crop(0, currentY, image.Width, height);

            var partName = $"{fileNameWithoutExt}_part{partIndex}{ext}";
            var partPath = Path.Combine(outputDir, partName);
            crop.WriteToFile(partPath);
            resultPaths.Add(partPath);

            currentY = splitY;
            partIndex++;
        }

        // Last part
        if (currentY < image.Height)
        {
            int height = image.Height - currentY;
            using var crop = image.Crop(0, currentY, image.Width, height);

            var partName = $"{fileNameWithoutExt}_part{partIndex}{ext}";
            var partPath = Path.Combine(outputDir, partName);
            crop.WriteToFile(partPath);
            resultPaths.Add(partPath);
        }

        return resultPaths;
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

    private async Task UpdateStateAsync(
        int chapterId,
        int version,
        SplitProcessingStatus status,
        CancellationToken cancellationToken
    )
    {
        var state = await dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapterId,
            cancellationToken
        );

        if (state != null)
        {
            state.Status = status;
            if (status == SplitProcessingStatus.Applied)
            {
                state.LastAppliedDetectorVersion = version;
            }
            state.ModifiedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
