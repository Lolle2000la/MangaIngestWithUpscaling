using System.IO.Compression;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[RegisterSingleton]
public class OnnxUpscaleEngine(
    IOnnxSessionFactory sessionFactory,
    IOptions<UpscalerConfig> config,
    ILogger<OnnxUpscaleEngine> logger
) : IOnnxUpscaleEngine
{
    private string ModelsDirectory => config.Value.ResolvedModelsDirectory;

    public async Task UpscaleFileAsync(
        string inputPath,
        string outputPath,
        int scale,
        CompressionFormat format,
        int? quality,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input image not found: {inputPath}", inputPath);
        }

        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var vipsImage = NetVips.Image.NewFromFile(
                    inputPath,
                    access: NetVips.Enums.Access.Random
                );
                bool isGrayscale = IsGrayscale(vipsImage);
                int origWidth = vipsImage.Width;
                int origHeight = vipsImage.Height;

                string modelPath = SelectModel(ModelsDirectory, isGrayscale, origHeight, scale);
                long modelSizeBytes = 0;
                try
                {
                    modelSizeBytes = new FileInfo(modelPath).Length;
                }
                catch { }

                using var flattened = vipsImage.HasAlpha() ? vipsImage.Flatten() : vipsImage.Copy();
                using var rgb =
                    flattened.Bands == 3
                    && flattened.Interpretation == NetVips.Enums.Interpretation.Srgb
                        ? flattened.Copy()
                        : flattened.Colourspace(NetVips.Enums.Interpretation.Srgb);
                using var ucharRgb =
                    rgb.Format == NetVips.Enums.BandFormat.Uchar
                        ? rgb.Copy()
                        : rgb.Cast(NetVips.Enums.BandFormat.Uchar);

                byte[] inputBytes = ucharRgb.WriteToMemory<byte>();

                cancellationToken.ThrowIfCancellationRequested();

                InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);
                bool isFp16 =
                    session.InputMetadata.TryGetValue("input", out var inputMeta)
                    && inputMeta.ElementType == typeof(Float16);

                long budget =
                    config.Value.MemoryBudgetBytes > 0
                        ? config.Value.MemoryBudgetBytes
                        : OnnxTiler.GetAvailableVramBudget();

                int effectiveTileSize =
                    config.Value.TileSize > 0 ? config.Value.TileSize
                    : config.Value.TileSize < 0 ? 0
                    : OnnxTiler.EstimateTileSize(
                        origWidth,
                        origHeight,
                        scale,
                        modelPath,
                        budget,
                        modelSizeBytes,
                        isFp16
                    );

                var (vramBefore, gttBefore) = OnnxTiler.GetGpuMemoryUsage();
                logger.LogDebug(
                    "Upscaling {InputPath} ({Width}x{Height}, isGrayscale={IsGrayscale}) using model {Model} with tile size {TileSize} (budget: {BudgetMb} MB, VRAM: {VramMb} MB, GTT: {GttMb} MB)",
                    Path.GetFileName(inputPath),
                    origWidth,
                    origHeight,
                    isGrayscale,
                    Path.GetFileName(modelPath),
                    effectiveTileSize == 0 ? "Full image" : effectiveTileSize.ToString(),
                    budget / (1024 * 1024),
                    vramBefore / (1024 * 1024),
                    gttBefore / (1024 * 1024)
                );

                byte[] upscaledBytes;
                try
                {
                    upscaledBytes = OnnxTiler.UpscaleRgb(
                        inputBytes,
                        origWidth,
                        origHeight,
                        scale,
                        session,
                        tileSize: effectiveTileSize,
                        cancellationToken: cancellationToken
                    );
                }
                catch (Exception)
                {
                    sessionFactory.InvalidateSession(modelPath);
                    throw;
                }

                var (vramAfter, gttAfter) = OnnxTiler.GetGpuMemoryUsage();
                if (gttAfter > 500L * 1024 * 1024)
                {
                    logger.LogInformation(
                        "GPU memory pressure for {InputPath}: VRAM {VramMb} MB, GTT {GttMb} MB",
                        Path.GetFileName(inputPath),
                        vramAfter / (1024 * 1024),
                        gttAfter / (1024 * 1024)
                    );
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var outImage = NetVips.Image.NewFromMemory(
                    upscaledBytes,
                    origWidth * scale,
                    origHeight * scale,
                    3,
                    NetVips.Enums.BandFormat.Uchar
                );

                SaveImage(outImage, outputPath, format, quality);
            },
            cancellationToken
        );
    }

    public async Task UpscaleCbzAsync(
        string inputCbzPath,
        string outputCbzPath,
        int scale,
        CompressionFormat format,
        int? quality,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException($"Input CBZ not found: {inputCbzPath}", inputCbzPath);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"cbz_upscale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempOutCbz = Path.Combine(tempDir, "output.cbz");

        try
        {
            using (var inArchive = ZipFile.OpenRead(inputCbzPath))
            using (var outArchive = ZipFile.Open(tempOutCbz, ZipArchiveMode.Create))
            {
                var entries = inArchive.Entries.ToList();
                var imageEntries = entries
                    .Where(e =>
                        ImageConstants.SupportedImageExtensions.Contains(
                            Path.GetExtension(e.FullName).ToLowerInvariant()
                        )
                    )
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = imageEntries.Count;
                int current = 0;

                string targetExt = format switch
                {
                    CompressionFormat.Avif => ".avif",
                    CompressionFormat.Webp => ".webp",
                    CompressionFormat.Jpg => ".jpg",
                    CompressionFormat.Png => ".png",
                    _ => ".webp",
                };

                // Copy non-image entries directly (e.g. ComicInfo.xml)
                foreach (var nonImg in entries.Where(e => !imageEntries.Contains(e)))
                {
                    if (string.IsNullOrEmpty(nonImg.Name))
                        continue; // skip directory entries
                    var newEntry = outArchive.CreateEntry(
                        nonImg.FullName,
                        CompressionLevel.Optimal
                    );
                    using var srcStream = nonImg.Open();
                    using var dstStream = newEntry.Open();
                    await srcStream.CopyToAsync(dstStream, cancellationToken);
                }

                // Process image entries
                foreach (var entry in imageEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(
                        new UpscaleProgress(total, current, "Upscaling", $"Upscaling {entry.Name}")
                    );

                    string tempInPage = Path.Combine(tempDir, $"in_{current}_{entry.Name}");
                    string tempOutPage = Path.Combine(tempDir, $"out_{current}{targetExt}");

                    entry.ExtractToFile(tempInPage, overwrite: true);

                    await UpscaleFileAsync(
                        tempInPage,
                        tempOutPage,
                        scale,
                        format,
                        quality,
                        cancellationToken
                    );

                    string outEntryName = Path.ChangeExtension(entry.FullName, targetExt);
                    outArchive.CreateEntryFromFile(
                        tempOutPage,
                        outEntryName,
                        CompressionLevel.Optimal
                    );

                    try
                    {
                        File.Delete(tempInPage);
                    }
                    catch { }
                    try
                    {
                        File.Delete(tempOutPage);
                    }
                    catch { }

                    current++;

                    progress?.Report(
                        new UpscaleProgress(total, current, "Upscaling", $"Upscaled {entry.Name}")
                    );
                }
            }

            string? finalDir = Path.GetDirectoryName(outputCbzPath);
            if (!string.IsNullOrEmpty(finalDir) && !Directory.Exists(finalDir))
            {
                Directory.CreateDirectory(finalDir);
            }

            if (File.Exists(outputCbzPath))
            {
                File.Delete(outputCbzPath);
            }

            File.Move(tempOutCbz, outputCbzPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
        }
    }

    public static bool IsGrayscale(NetVips.Image image)
    {
        if (
            image.Bands == 1
            || image.Interpretation
                is NetVips.Enums.Interpretation.Bw
                    or NetVips.Enums.Interpretation.Grey16
        )
        {
            return true;
        }

        if (image.Bands >= 3)
        {
            using var r = image[0];
            using var g = image[1];
            using var b = image[2];
            using var diff1 = (r - g).Abs();
            using var diff2 = (g - b).Abs();
            double maxDiff = Math.Max(diff1.Max(), diff2.Max());
            return maxDiff <= 2.0;
        }

        return false;
    }

    public static string SelectModel(
        string modelsDirectory,
        bool isGrayscale,
        int height,
        int scale
    )
    {
        if (!Directory.Exists(modelsDirectory))
        {
            throw new DirectoryNotFoundException($"Models directory not found: {modelsDirectory}");
        }

        string[] onnxFiles = Directory.GetFiles(modelsDirectory, "*.onnx");
        if (onnxFiles.Length == 0)
        {
            throw new FileNotFoundException(
                $"No ONNX models found in models directory: {modelsDirectory}"
            );
        }

        string scalePrefix = $"{scale}x_";

        if (isGrayscale)
        {
            string res;
            if (height <= 1250)
                res = "1200p";
            else if (height <= 1350)
                res = "1300p";
            else if (height <= 1450)
                res = "1400p";
            else if (height <= 1550)
                res = "1500p";
            else if (height <= 1760)
                res = "1600p";
            else if (height <= 1984)
                res = "1920p";
            else
                res = "2048p";

            string targetKey = $"{scalePrefix}MangaJaNai_{res}";
            string? match = onnxFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith(targetKey, StringComparison.OrdinalIgnoreCase)
            );
            if (match != null)
                return match;

            string mangaFallbackKey = $"{scalePrefix}MangaJaNai_";
            match = onnxFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith(mangaFallbackKey, StringComparison.OrdinalIgnoreCase)
            );
            if (match != null)
                return match;
        }
        else
        {
            string colorKey = $"{scalePrefix}IllustrationJaNai_";
            string[] preferred =
            [
                $"{scalePrefix}IllustrationJaNai_V3detail_",
                $"{scalePrefix}IllustrationJaNai_V3denoise_",
                $"{scalePrefix}IllustrationJaNai_V2standard_",
                $"{scalePrefix}IllustrationJaNai_V1_",
            ];

            foreach (var pref in preferred)
            {
                string? match = onnxFiles.FirstOrDefault(f =>
                    Path.GetFileName(f).StartsWith(pref, StringComparison.OrdinalIgnoreCase)
                );
                if (match != null)
                    return match;
            }

            string? colorMatch = onnxFiles.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith(colorKey, StringComparison.OrdinalIgnoreCase)
            );
            if (colorMatch != null)
                return colorMatch;
        }

        string? genericMatch = onnxFiles.FirstOrDefault(f =>
            Path.GetFileName(f).StartsWith(scalePrefix, StringComparison.OrdinalIgnoreCase)
        );
        if (genericMatch != null)
            return genericMatch;

        return onnxFiles[0];
    }

    private static void SaveImage(
        NetVips.Image image,
        string outputPath,
        CompressionFormat format,
        int? quality
    )
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        switch (format)
        {
            case CompressionFormat.Avif:
                image.Heifsave(
                    outputPath,
                    q: quality ?? 80,
                    compression: NetVips.Enums.ForeignHeifCompression.Av1
                );
                break;
            case CompressionFormat.Webp:
                image.Webpsave(outputPath, q: quality ?? 80);
                break;
            case CompressionFormat.Jpg:
                image.Jpegsave(outputPath, q: quality ?? 85);
                break;
            case CompressionFormat.Png:
                image.Pngsave(outputPath);
                break;
            default:
                image.WriteToFile(outputPath);
                break;
        }
    }
}
