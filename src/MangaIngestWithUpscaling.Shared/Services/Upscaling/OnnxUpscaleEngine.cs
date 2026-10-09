using System.IO.Compression;
using System.Runtime.InteropServices;
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
    IDeviceMemoryCalibrator deviceCalibrator,
    ILogger<OnnxUpscaleEngine> logger
) : IOnnxUpscaleEngine
{
    private string ModelsDirectory => config.Value.ResolvedModelsDirectory;

    private readonly record struct UpscaledPage(
        byte[] Bytes,
        int Width,
        int Height,
        bool IsGrayscale
    );

    public async Task UpscaleFileAsync(
        string inputPath,
        string outputPath,
        int scale,
        CompressionFormat format,
        int? quality,
        CancellationToken cancellationToken
    )
    {
        Task writeTask = await UpscaleFileStagedAsync(
            inputPath,
            outputPath,
            scale,
            format,
            quality,
            cancellationToken
        );
        await writeTask;
    }

    public async Task<Task> UpscaleFileStagedAsync(
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

        UpscaledPage upscaled = await Task.Run(
            async () =>
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

                int deviceId = Math.Max(0, config.Value.SelectedDeviceIndex - 1);
                long budget =
                    config.Value.MemoryBudgetBytes > 0
                        ? config.Value.MemoryBudgetBytes
                        : OnnxTiler.GetAvailableVramBudget(
                            deviceId,
                            config.Value.VramUtilizationFraction,
                            config.Value.VramSafetyMarginBytes
                        );

                // First upscale on a device runs a short benchmark so tile sizing uses what this
                // accelerator does rather than a generic coefficient. Cached on disk per device.
                DeviceMemoryProfile? profile = await deviceCalibrator.GetOrCalibrateAsync(
                    deviceId,
                    ModelsDirectory,
                    cancellationToken
                );

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
                        isFp16,
                        profile
                    );

                var (vramBefore, gttBefore) = OnnxTiler.GetGpuMemoryUsage(deviceId);
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
                int attemptTileSize = effectiveTileSize;
                while (true)
                {
                    try
                    {
                        session = sessionFactory.GetOrCreateSession(modelPath);
                        using (sessionFactory.EnterInferenceScope())
                        {
                            upscaledBytes = OnnxTiler.UpscaleRgb(
                                inputBytes,
                                origWidth,
                                origHeight,
                                scale,
                                session,
                                tileSize: attemptTileSize,
                                cancellationToken: cancellationToken
                            );
                        }
                        break;
                    }
                    catch (Exception ex)
                        when (OnnxTiler.IsMemoryException(ex) && attemptTileSize > 128)
                    {
                        logger.LogWarning(
                            ex,
                            "Memory pressure encountered upscaling {Input} with tile size {TileSize}. Halving tile size and recreating session.",
                            Path.GetFileName(inputPath),
                            attemptTileSize
                        );
                        sessionFactory.InvalidateSession(modelPath);
                        attemptTileSize = Math.Max(128, attemptTileSize / 2);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    catch (Exception)
                    {
                        sessionFactory.InvalidateSession(modelPath);
                        throw;
                    }
                }

                var (vramAfter, gttAfter) = OnnxTiler.GetGpuMemoryUsage(deviceId);
                if (gttAfter > 1000L * 1024 * 1024)
                {
                    logger.LogWarning(
                        "Elevated GPU memory detected after upscaling {InputPath}: VRAM {VramMb} MB, GTT {GttMb} MB. Flushing session cache to release memory.",
                        Path.GetFileName(inputPath),
                        vramAfter / (1024 * 1024),
                        gttAfter / (1024 * 1024)
                    );
                    sessionFactory.InvalidateSession(modelPath);
                    GC.Collect();
                }

                cancellationToken.ThrowIfCancellationRequested();

                return new UpscaledPage(
                    upscaledBytes,
                    origWidth * scale,
                    origHeight * scale,
                    isGrayscale
                );
            },
            cancellationToken
        );

        // Encoding is CPU-only (and takes seconds for a 4x page). Handing it back as a separate
        // task lets the caller start the next page's inference instead of leaving the GPU idle.
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteUpscaledPage(upscaled, outputPath, format, quality);
            },
            cancellationToken
        );
    }

    private static void WriteUpscaledPage(
        UpscaledPage page,
        string outputPath,
        CompressionFormat format,
        int? quality
    )
    {
        using var outImage = NetVips.Image.NewFromMemory(
            page.Bytes,
            page.Width,
            page.Height,
            3,
            NetVips.Enums.BandFormat.Uchar
        );
        using var srgbImg = outImage.Copy(interpretation: NetVips.Enums.Interpretation.Srgb);

        if (page.IsGrayscale)
        {
            using var bwImg = srgbImg[0].Copy(interpretation: NetVips.Enums.Interpretation.Bw);
            SaveImage(bwImg, outputPath, format, quality);
        }
        else
        {
            SaveImage(srgbImg, outputPath, format, quality);
        }
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

                // Pipelined: while page N runs on the GPU, page N-1 finishes encoding and is added to
                // the archive. Only this loop touches outArchive, so no extra synchronization is needed.
                (ZipArchiveEntry Entry, string TempIn, string TempOut, Task Write)? pending = null;

                async Task FinalizePendingAsync()
                {
                    if (pending is not { } p)
                    {
                        return;
                    }
                    pending = null;

                    try
                    {
                        await p.Write;
                        outArchive.CreateEntryFromFile(
                            p.TempOut,
                            Path.ChangeExtension(p.Entry.FullName, targetExt),
                            CompressionLevel.Optimal
                        );
                    }
                    finally
                    {
                        TryDeleteFile(p.TempIn);
                        TryDeleteFile(p.TempOut);
                    }

                    current++;
                    progress?.Report(
                        new UpscaleProgress(total, current, "Upscaling", $"Upscaled {p.Entry.Name}")
                    );
                }

                try
                {
                    // Process image entries
                    foreach (var entry in imageEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        progress?.Report(
                            new UpscaleProgress(
                                total,
                                current,
                                "Upscaling",
                                $"Upscaling {entry.Name}"
                            )
                        );

                        string pageId = (current + (pending.HasValue ? 1 : 0)).ToString();
                        string tempInPage = Path.Combine(tempDir, $"in_{pageId}_{entry.Name}");
                        string tempOutPage = Path.Combine(tempDir, $"out_{pageId}{targetExt}");

                        entry.ExtractToFile(tempInPage, overwrite: true);

                        Task<Task> inference = UpscaleFileStagedAsync(
                            tempInPage,
                            tempOutPage,
                            scale,
                            format,
                            quality,
                            cancellationToken
                        );

                        try
                        {
                            await FinalizePendingAsync();
                        }
                        catch
                        {
                            // Never leave inference or an encode in flight while the temp dir is torn down.
                            await ObserveStagedAsync(inference);
                            TryDeleteFile(tempInPage);
                            TryDeleteFile(tempOutPage);
                            throw;
                        }

                        Task write;
                        try
                        {
                            write = await inference;
                        }
                        catch
                        {
                            TryDeleteFile(tempInPage);
                            TryDeleteFile(tempOutPage);
                            throw;
                        }
                        pending = (entry, tempInPage, tempOutPage, write);
                    }

                    await FinalizePendingAsync();
                }
                finally
                {
                    if (pending is { } leftover)
                    {
                        pending = null;
                        await ObserveAsync(leftover.Write);
                        TryDeleteFile(leftover.TempIn);
                        TryDeleteFile(leftover.TempOut);
                    }
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

            // Invalidate cached sessions after a chapter completes to return all GPU VRAM and GTT
            // buffers back to the driver. This guarantees zero cumulative memory creep across chapters.
            sessionFactory.InvalidateAllSessions();
            GC.Collect();
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

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch { }
    }

    private static async Task ObserveStagedAsync(Task<Task> staged)
    {
        try
        {
            await await staged;
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
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
            if (maxDiff <= 2.0)
            {
                return true;
            }

            // In scanned manga or digital releases with JPEG compression, isolated chroma noise or subtle paper tint
            // can produce small max differences across isolated pixels even though the entire page is grayscale.
            // Check average channel divergence: grayscale pages have mean divergence < 0.5, while color pages have >> 2.0.
            if (maxDiff <= 25.0)
            {
                double meanDiff = Math.Max(diff1.Avg(), diff2.Avg());
                return meanDiff <= 0.5;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Infers the upscale factor from a model file name (<c>2x_...</c> / <c>4x_...</c>). Returns 0
    /// when the name does not declare one, so callers can treat that as "unknown".
    /// </summary>
    public static int InferScaleFromModelName(string modelNameOrPath)
    {
        string name = Path.GetFileNameWithoutExtension(modelNameOrPath);
        if (
            name.Length >= 2
            && name[1] == 'x'
            && char.IsDigit(name[0])
            && int.TryParse(name[0..1], out int scale)
        )
        {
            return scale;
        }

        return 0;
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
            string[] preferredVersions =
            [
                $"{scalePrefix}IllustrationJaNai_V3detail_",
                $"{scalePrefix}IllustrationJaNai_V3denoise_",
                $"{scalePrefix}IllustrationJaNai_V2standard_",
                $"{scalePrefix}IllustrationJaNai_V1_",
            ];

            // Architectures ordered by WebGPU/GPU compatibility and inference speed:
            // Prioritize fast, native architectures (FDAT_M, ESRGAN, SPAN) over experimental/heavy models (DAT2, HAT_L).
            // Avoid _bf16 models which cannot run natively on WebGPU execution provider.
            string[] preferredArchs = ["FDAT_M", "ESRGAN", "SPAN", "FDAT_XL", "DAT2", "HAT_L"];

            foreach (var pref in preferredVersions)
            {
                var candidates = onnxFiles
                    .Where(f =>
                        Path.GetFileName(f).StartsWith(pref, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();

                if (candidates.Count > 0)
                {
                    foreach (var arch in preferredArchs)
                    {
                        var nonBf16Match = candidates.FirstOrDefault(f =>
                            f.Contains(arch, StringComparison.OrdinalIgnoreCase)
                            && !f.Contains("bf16", StringComparison.OrdinalIgnoreCase)
                        );
                        if (nonBf16Match != null)
                            return nonBf16Match;

                        var anyArchMatch = candidates.FirstOrDefault(f =>
                            f.Contains(arch, StringComparison.OrdinalIgnoreCase)
                        );
                        if (anyArchMatch != null)
                            return anyArchMatch;
                    }

                    return candidates[0];
                }
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
