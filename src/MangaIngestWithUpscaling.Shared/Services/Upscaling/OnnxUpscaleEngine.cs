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

    /// <summary>
    /// Side length of the tile used to find out whether a model can run on this device.
    /// <para>
    /// 256 px is the smallest size at which every one of the shipped transformer models that the
    /// WebGPU EP cannot run already reports the failure — several of them only produce valid
    /// output up to 128 px — while still costing well under a second of inference. A model that
    /// only breaks at a larger tile is still caught, by the per-tile guard on the page itself.
    /// </para>
    /// </summary>
    private const int ModelProbeTilePixels = 256;

    /// <summary>
    /// Verdict per model for this device, keyed by full path. Scoped to the process: whether a
    /// model runs is a property of the execution provider and the accelerator, neither of which
    /// changes while the process runs.
    /// </summary>
    private readonly Dictionary<string, bool> _modelUsable = new(StringComparer.OrdinalIgnoreCase);

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

                // A page resolves to a preference list rather than one model: whether the most
                // preferred model can run at all is a property of the execution provider and
                // the device, and is only discovered when it returns valid output.
                IReadOnlyList<string> candidates = SelectModelCandidates(
                    ModelsDirectory,
                    isGrayscale,
                    origHeight,
                    scale
                );

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

                int deviceId = Math.Max(0, config.Value.SelectedDeviceIndex - 1);
                long budget =
                    config.Value.ResolvedMemoryBudgetBytes > 0
                        ? config.Value.ResolvedMemoryBudgetBytes
                        : OnnxTiler.GetAvailableVramBudget(
                            deviceId,
                            config.Value.VramUtilizationFraction,
                            config.Value.ResolvedVramSafetyMarginBytes,
                            config.Value.ResolvedVramExclusiveThresholdBytes
                        );

                // First upscale on a device runs a short benchmark so tile sizing uses what this
                // accelerator does rather than a generic coefficient. Cached on disk per device.
                DeviceMemoryProfile? profile = await deviceCalibrator.GetOrCalibrateAsync(
                    deviceId,
                    ModelsDirectory,
                    cancellationToken
                );

                (string model, byte[] upscaledBytes) = await UpscaleWithFirstWorkingModelAsync(
                    inputPath,
                    candidates,
                    inputBytes,
                    origWidth,
                    origHeight,
                    scale,
                    deviceId,
                    budget,
                    profile,
                    cancellationToken
                );

                UpscaledPage page = new(
                    upscaledBytes,
                    origWidth * scale,
                    origHeight * scale,
                    isGrayscale
                );

                var (vramAfter, gttAfter) = OnnxTiler.GetGpuMemoryUsage(deviceId);
                if (gttAfter > 1000L * 1024 * 1024)
                {
                    logger.LogWarning(
                        "Elevated GPU memory detected after upscaling {InputPath}: VRAM {VramMb} MB, GTT {GttMb} MB. Flushing session cache to release memory.",
                        Path.GetFileName(inputPath),
                        vramAfter / (1024 * 1024),
                        gttAfter / (1024 * 1024)
                    );
                    sessionFactory.InvalidateSession(model);
                    GC.Collect();
                }

                cancellationToken.ThrowIfCancellationRequested();

                return page;
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

    /// <summary>
    /// Models that can upscale a page, most preferred first.
    /// <para>
    /// A page's classification resolves to a preference list rather than a single model because
    /// the preference is a guess about quality only: whether a model actually runs is a property
    /// of the execution provider and the device, and is only known once that model has produced
    /// finite output there. <see cref="SelectModel"/> remains the first choice.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SelectModelCandidates(
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
        var candidates = new List<string>();

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

            AddMatch(candidates, onnxFiles, $"{scalePrefix}MangaJaNai_{res}");
            AddMatch(candidates, onnxFiles, $"{scalePrefix}MangaJaNai_");

            // A grayscale page still upscales fine with an illustration model: that is a model
            // trained on colour, not a model that only accepts colour input.
            AddMatch(candidates, onnxFiles, $"{scalePrefix}IllustrationJaNai_");
        }
        else
        {
            string colorKey = $"{scalePrefix}IllustrationJaNai_";
            string[] preferredVersions =
            [
                $"{colorKey}V3detail_",
                $"{colorKey}V3denoise_",
                $"{colorKey}V2standard_",
                $"{colorKey}V1_",
            ];

            // Architectures ordered by WebGPU/GPU compatibility and inference speed:
            // Prioritize fast, native architectures (FDAT_M, ESRGAN, SPAN) over experimental/heavy models (DAT2, HAT_L).
            // Avoid _bf16 models which cannot run natively on WebGPU execution provider.
            string[] preferredArchs = ["FDAT_M", "ESRGAN", "SPAN", "FDAT_XL", "DAT2", "HAT_L"];

            foreach (var pref in preferredVersions)
            {
                var versionCandidates = onnxFiles
                    .Where(f =>
                        Path.GetFileName(f).StartsWith(pref, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();

                foreach (var arch in preferredArchs)
                {
                    AddContaining(candidates, versionCandidates, arch, NotBf16);
                    AddContaining(candidates, versionCandidates, arch, _ => true);
                }

                AddAll(candidates, versionCandidates);
            }

            AddMatch(candidates, onnxFiles, colorKey);
        }

        // Last resort: anything else at this scale, then anything at all. Both are ordered by
        // name so the choice does not depend on the order the file system happens to return.
        AddAll(
            candidates,
            onnxFiles
                .Where(f =>
                    Path.GetFileName(f).StartsWith(scalePrefix, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        );
        AddAll(candidates, onnxFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));

        return candidates;
    }

    private static bool NotBf16(string path) =>
        !Path.GetFileName(path).Contains("bf16", StringComparison.OrdinalIgnoreCase);

    public static string SelectModel(
        string modelsDirectory,
        bool isGrayscale,
        int height,
        int scale
    ) => SelectModelCandidates(modelsDirectory, isGrayscale, height, scale)[0];

    private static void AddMatch(
        List<string> candidates,
        IEnumerable<string> files,
        string keyPrefix
    ) => AddMatch(candidates, files, keyPrefix, _ => true);

    private static void AddMatch(
        List<string> candidates,
        IEnumerable<string> files,
        string keyPrefix,
        Func<string, bool> filter
    )
    {
        foreach (var file in files)
        {
            if (
                Path.GetFileName(file).StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)
                && filter(file)
                && !candidates.Contains(file, StringComparer.OrdinalIgnoreCase)
            )
            {
                candidates.Add(file);
            }
        }
    }

    private static void AddContaining(
        List<string> candidates,
        IEnumerable<string> files,
        string token,
        Func<string, bool> filter
    )
    {
        foreach (var file in files)
        {
            if (
                Path.GetFileName(file).Contains(token, StringComparison.OrdinalIgnoreCase)
                && filter(file)
                && !candidates.Contains(file, StringComparer.OrdinalIgnoreCase)
            )
            {
                candidates.Add(file);
            }
        }
    }

    private static void AddAll(List<string> candidates, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (!candidates.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(file);
            }
        }
    }

    /// <summary>
    /// Upscales a page with the first model in <paramref name="candidates"/> that actually works
    /// on this device, and skips the rest for the lifetime of the process once a model is known
    /// not to.
    /// <para>
    /// This is what stops an unusable model from becoming a black page: the WebGPU EP does not
    /// implement the transformer kernels and returns NaN instead of failing, so the only way to
    /// find out is to run it and check.
    /// </para>
    /// </summary>
    private async Task<(string Model, byte[] Bytes)> UpscaleWithFirstWorkingModelAsync(
        string inputPath,
        IReadOnlyList<string> candidates,
        byte[] inputBytes,
        int width,
        int height,
        int scale,
        int deviceId,
        long budget,
        DeviceMemoryProfile? profile,
        CancellationToken cancellationToken
    )
    {
        var unusable = new List<string>();

        foreach (string modelPath in candidates)
        {
            if (!await ModelUsableOnThisDeviceAsync(modelPath, scale, cancellationToken))
            {
                continue;
            }

            try
            {
                byte[] upscaled = await UpscaleWithModelAsync(
                    inputPath,
                    modelPath,
                    inputBytes,
                    width,
                    height,
                    scale,
                    deviceId,
                    budget,
                    profile,
                    cancellationToken
                );
                return (modelPath, upscaled);
            }
            catch (NonFiniteModelOutputException ex)
            {
                MarkModelUnusable(modelPath);
                unusable.Add(Path.GetFileName(modelPath));
                logger.LogWarning(
                    ex,
                    "Model {Model} produced non-finite output on device {Device} with the {Provider} execution provider, so it would have written a black page. Trying the next model.",
                    Path.GetFileName(modelPath),
                    deviceId,
                    sessionFactory.GetEffectiveBackend()
                );
            }
        }

        throw new InvalidOperationException(
            $"No model in {ModelsDirectory} can upscale {width}x{height} at {scale}x: "
                + (
                    unusable.Count > 0
                        ? $"{string.Join(", ", unusable)} produced non-finite output."
                        : "every candidate model is already known to be unusable on this device."
                )
        );
    }

    /// <summary>
    /// Runs one model over the whole page, halving the tiles as often as the driver asks for more
    /// memory. The tile plan follows the model, because a grid that is affordable for one is not
    /// necessarily affordable for another.
    /// </summary>
    private async Task<byte[]> UpscaleWithModelAsync(
        string inputPath,
        string modelPath,
        byte[] inputBytes,
        int width,
        int height,
        int scale,
        int deviceId,
        long budget,
        DeviceMemoryProfile? profile,
        CancellationToken cancellationToken
    ) =>
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                long modelSizeBytes = 0;
                try
                {
                    modelSizeBytes = new FileInfo(modelPath).Length;
                }
                catch { }

                InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);
                bool isFp16 =
                    session.InputMetadata.TryGetValue("input", out var inputMeta)
                    && inputMeta.ElementType == typeof(Float16);

                OnnxTiler.TileSplit split =
                    config.Value.TileSize > 0
                        ? OnnxTiler.TileSplit.For(width, height, config.Value.TileSize)
                    : config.Value.TileSize < 0 ? OnnxTiler.TileSplit.For(width, height, 0)
                    : OnnxTiler.PlanTileSplit(
                        width,
                        height,
                        scale,
                        modelPath,
                        budget,
                        modelSizeBytes,
                        isFp16,
                        profile
                    );

                var (vramBefore, gttBefore) = OnnxTiler.GetGpuMemoryUsage(deviceId);
                logger.LogDebug(
                    "Upscaling {InputPath} ({Width}x{Height}, scale {Scale}) using model {Model} in {Columns}x{Rows} tiles of {TileWidth}x{TileHeight} (budget: {BudgetMb} MB, VRAM: {VramMb} MB, GTT: {GttMb} MB)",
                    Path.GetFileName(inputPath),
                    width,
                    height,
                    scale,
                    Path.GetFileName(modelPath),
                    split.Columns,
                    split.Rows,
                    split.TileWidth,
                    split.TileHeight,
                    budget / (1024 * 1024),
                    vramBefore / (1024 * 1024),
                    gttBefore / (1024 * 1024)
                );

                byte[] upscaledBytes;
                OnnxTiler.TileSplit attempt = split;
                while (true)
                {
                    try
                    {
                        session = sessionFactory.GetOrCreateSession(modelPath);
                        using (sessionFactory.EnterInferenceScope())
                        {
                            upscaledBytes = OnnxTiler.UpscaleRgb(
                                inputBytes,
                                width,
                                height,
                                scale,
                                session,
                                attempt,
                                cancellationToken: cancellationToken
                            );
                        }
                        break;
                    }
                    catch (Exception ex)
                        when (OnnxTiler.IsMemoryException(ex) && attempt.TileWidth > 128)
                    {
                        logger.LogWarning(
                            ex,
                            "Memory pressure encountered upscaling in {Columns}x{Rows} tiles of {TileWidth}x{TileHeight}. Halving tile size and recreating session.",
                            attempt.Columns,
                            attempt.Rows,
                            attempt.TileWidth,
                            attempt.TileHeight
                        );
                        sessionFactory.InvalidateSession(modelPath);
                        attempt = OnnxTiler.HalveSplit(width, height, attempt);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    catch (Exception)
                    {
                        sessionFactory.InvalidateSession(modelPath);
                        throw;
                    }
                }

                return upscaledBytes;
            },
            cancellationToken
        );

    /// <summary>
    /// Whether this model can run on the configured device, remembered for the process after the
    /// first answer.
    /// <para>
    /// The question is asked with one small tile through the real path, because there is no other
    /// way to know: an execution provider that does not implement a model's kernels does not say
    /// so at session creation, it returns NaN from the first inference. Running that check on a
    /// 64x64 tile costs milliseconds, while running it on the page costs the whole page.
    /// </para>
    /// </summary>
    private async Task<bool> ModelUsableOnThisDeviceAsync(
        string modelPath,
        int scale,
        CancellationToken cancellationToken
    )
    {
        lock (_modelUsable)
        {
            if (_modelUsable.TryGetValue(modelPath, out bool known))
            {
                return known;
            }
        }

        bool usable;
        try
        {
            usable = await Task.Run(
                () =>
                {
                    InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);

                    // The decode is what rejects a broken model, so the probe goes through it.
                    OnnxTiler.UpscaleTile(
                        CreateProbeTile(ModelProbeTilePixels),
                        ModelProbeTilePixels,
                        ModelProbeTilePixels,
                        scale,
                        session,
                        cancellationToken
                    );
                    return true;
                },
                cancellationToken
            );
        }
        catch (NonFiniteModelOutputException)
        {
            sessionFactory.InvalidateSession(modelPath);
            usable = false;
        }

        lock (_modelUsable)
        {
            _modelUsable[modelPath] = usable;
        }

        return usable;
    }

    /// <summary>
    /// A deterministic RGB gradient. Content does not matter for the probe — it only asks whether
    /// the model returns numbers at all — but a flat tile would not exercise a kernel that needs
    /// variation.
    /// </summary>
    private static byte[] CreateProbeTile(int size)
    {
        byte[] tile = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = (y * size + x) * 3;
                tile[idx] = (byte)(x * 255 / (size - 1));
                tile[idx + 1] = (byte)(y * 255 / (size - 1));
                tile[idx + 2] = (byte)((x + y) * 255 / (2 * size - 2));
            }
        }

        return tile;
    }

    private void MarkModelUnusable(string modelPath)
    {
        lock (_modelUsable)
        {
            _modelUsable[modelPath] = false;
        }
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
