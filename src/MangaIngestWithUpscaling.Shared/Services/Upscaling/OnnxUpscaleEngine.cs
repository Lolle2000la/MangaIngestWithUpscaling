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
    /// Upper bound on the probe-verdict cache. The number of models times the scales in play is
    /// small, but candidate selection also probes unrelated <c>.onnx</c> files as a last resort, so
    /// the cache is capped rather than trusted to stay bounded. Overflow clears it and restarts,
    /// which only costs the next page a re-probe — never a wrong answer.
    /// </summary>
    private const int ModelUsableCacheCapacity = 256;

    /// <summary>
    /// GTT growth during one page above which the cached session is dropped after the page. The
    /// per-shape buffer cache is handled proactively (see <see cref="_sessionShape"/>), so this is a
    /// safety net for anything that still grows; a page on a clean session measures under 100 MB of
    /// growth, and a second page at a new shape measured 3.9 GB.
    /// </summary>
    private const long ElevatedGttGrowthBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Verdict per model <em>and scale</em> for this device, keyed by path and scale. Scoped to the
    /// process: whether a model runs is a property of the execution provider and the accelerator,
    /// neither of which changes while the process runs. The scale is part of the key because the
    /// probe runs through the decode, which rejects an output whose geometry does not match the
    /// requested scale: a 4x page can probe a 2x model (candidate selection appends every
    /// <c>.onnx</c> as a last resort), and that verdict must not disable the model for a 2x page.
    /// </summary>
    private readonly Dictionary<ModelScaleKey, bool> _modelUsable = new(
        ModelScaleKeyComparer.Instance
    );

    /// <summary>Path and scale a probe verdict applies to.</summary>
    private readonly record struct ModelScaleKey(string Path, int Scale);

    /// <summary>
    /// Compares keys case-insensitively on the path, matching how the rest of the engine resolves
    /// model paths (candidate selection and the model-file checks are all
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>).
    /// </summary>
    private sealed class ModelScaleKeyComparer : IEqualityComparer<ModelScaleKey>
    {
        public static readonly ModelScaleKeyComparer Instance = new();

        public bool Equals(ModelScaleKey x, ModelScaleKey y) =>
            x.Scale == y.Scale && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ModelScaleKey obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.Scale);
    }

    /// <summary>
    /// Padded tensor shape the cached session was last used at, and the model it belonged to. The
    /// WebGPU EP caches its buffers per tensor shape and exposes no API to release them, so a page
    /// whose tile shape differs from the previous one adds a second set to the cache instead of
    /// reusing the first. Measured on an RX 9070 XT with the fp16 ESRGAN at a 13 GB budget: the
    /// second page in a session, at a different shape, spilled 3.9 GB into GTT and took 25 s, while
    /// five same-shape pages in one session stayed at 82-89 MB and 7 s each. Dropping the session
    /// first costs a ~300 ms rebuild and keeps the page on the clean path.
    /// </summary>
    private readonly Lock _sessionShapeGate = new();
    private string? _sessionShapeModel;
    private (int Width, int Height) _sessionShape;

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
                // accelerator does rather than a generic coefficient. The benchmark is per model, so
                // it happens where the model is known (UpscaleWithModelAsync) rather than here.

                var (_, gttBefore) = OnnxTiler.GetGpuMemoryUsage(deviceId);

                (string model, byte[] upscaledBytes) = await UpscaleWithFirstWorkingModelAsync(
                    inputPath,
                    candidates,
                    inputBytes,
                    origWidth,
                    origHeight,
                    scale,
                    deviceId,
                    budget,
                    cancellationToken
                );

                UpscaledPage page = new(
                    upscaledBytes,
                    origWidth * scale,
                    origHeight * scale,
                    isGrayscale
                );

                // Compared against the reading taken before the page, not an absolute threshold: the
                // GTT baseline alone already sits near 1 GB on a desktop, so an absolute figure fires
                // on every page and turns the per-shape buffer cache's own reuse into a rebuild per
                // page. The per-shape growth is handled proactively in UpscaleWithModelAsync; this is
                // the safety net for anything that still grows.
                var (vramAfter, gttAfter) = OnnxTiler.GetGpuMemoryUsage(deviceId);
                if (gttAfter - gttBefore > ElevatedGttGrowthBytes)
                {
                    logger.LogWarning(
                        "Elevated GPU memory growth after upscaling {InputPath}: VRAM {VramMb} MB, GTT {GttMb} MB (+{GttGrowthMb} MB). Flushing session cache to release memory.",
                        Path.GetFileName(inputPath),
                        vramAfter / (1024 * 1024),
                        gttAfter / (1024 * 1024),
                        (gttAfter - gttBefore) / (1024 * 1024)
                    );
                    // Synchronous release, not InvalidateSession: the log says the cache is flushed to
                    // free memory, and the deferred retire only marks the session for a background
                    // drain — the footprint would stay up through the next page, which then budgets
                    // against it. No inference scope is held here (the page's own ended with
                    // UpscaleWithModelAsync), which is what makes the write lock safe to take.
                    sessionFactory.ReleaseCachedSessions();
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

                // Encode to a sibling temp and move only once it is complete. vips writes the file
                // incrementally, so a codec or disk failure part-way would otherwise leave a
                // truncated image where the destination should be. The CBZ path stages its archive
                // the same way for the same reason. The extension is preserved because the fallback
                // branch of SaveImage picks the format from it.
                string partial = PartialPath(outputPath);
                try
                {
                    WriteUpscaledPage(upscaled, partial, format, quality);
                    File.Move(partial, outputPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(partial))
                    {
                        try
                        {
                            File.Delete(partial);
                        }
                        catch { }
                    }
                }
            },
            cancellationToken
        );
    }

    /// <summary>
    /// A sibling path the encoder writes to before the finished file is moved onto
    /// <paramref name="outputPath"/>. Kept in the same directory so the final <c>File.Move</c> stays
    /// on one volume and is atomic, and keeps the extension so the format-by-extension fallback in
    /// <see cref="SaveImage"/> still works.
    /// </summary>
    private static string PartialPath(string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(outputPath);
        string extension = Path.GetExtension(outputPath);
        return Path.Combine(directory, $"{name}.part-{Guid.NewGuid():N}{extension}");
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

        // The archive is staged next to its destination, not in tempDir: the library and /tmp are
        // different volumes in the common container/NAS layout, so publishing it out of tempDir would
        // be a cross-device copy, and a crash part-way would leave a truncated CBZ where the chapter
        // should be. A sibling path makes the publish an atomic rename on one volume. The per-page
        // scratch stays in tempDir, which is never published.
        string? finalDir = Path.GetDirectoryName(outputCbzPath);
        if (!string.IsNullOrEmpty(finalDir))
        {
            Directory.CreateDirectory(finalDir);
        }
        string tempOutCbz = PartialPath(outputCbzPath);

        try
        {
            using (var inArchive = ZipFile.OpenRead(inputCbzPath))
            using (var outArchive = ZipFile.Open(tempOutCbz, ZipArchiveMode.Create))
            {
                // The one extension mapping both upscale paths share, so a format never resolves to
                // ".jpg" here and ".jpeg" on the streamed path.
                string targetExt = MangaJaNaiWorkerClient.ToExtension(format);

                var entries = new List<ZipArchiveEntry>(inArchive.Entries.Count);
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in inArchive.Entries)
                {
                    // An entry name that escapes the archive (absolute or parent-traversing) must
                    // never be written into the output: Path.ChangeExtension/CreateEntry preserve
                    // it verbatim, so a reader extracting the result could be made to write outside
                    // its target. Directory entries (empty Name) carry no name to escape with and
                    // are skipped by the copy loop below. The streamed path applies the same guard
                    // in PageStreamSpool.IsSafeEntryName, which this Shared assembly cannot reach.
                    if (string.IsNullOrEmpty(entry.Name) || IsSafeEntryName(entry.FullName))
                    {
                        // First occurrence wins, matching the streamed path
                        // (PageStreamSpool.AssembleCore), so a malformed archive that repeats an entry
                        // name yields the same page count on both paths.
                        if (seenNames.Add(entry.FullName))
                        {
                            entries.Add(entry);
                        }
                        continue;
                    }

                    logger.LogWarning(
                        "Skipping archive entry {Entry} of {InputPath}: its name could escape the output archive.",
                        entry.FullName,
                        Path.GetFileName(inputCbzPath)
                    );
                }
                // Natural order, not ordinal: page order is content-critical and an ordinal
                // comparison would place "10" before "2".
                var imageEntries = entries
                    .Where(e =>
                        ImageConstants.SupportedImageExtensions.Contains(
                            Path.GetExtension(e.FullName).ToLowerInvariant()
                        )
                    )
                    .OrderBy(e => e.FullName, NaturalEntryNameComparer.Instance)
                    .ToList();

                // Resolve the output name per entry, preserving the entry's folder. Two entries in
                // one folder can share a stem (001.jpg + 001.png) and thus an output name; a zip
                // entry name is not unique, so a name-keyed reader would keep only one and lose a
                // page. The page-streaming path disambiguates the same way; mirror it here.
                var outputNames = new HashSet<string>(StringComparer.Ordinal);
                var pages = new List<(ZipArchiveEntry Entry, string OutputName)>(
                    imageEntries.Count
                );
                foreach (var entry in imageEntries)
                {
                    string outputName = Path.ChangeExtension(entry.FullName, targetExt);
                    if (!outputNames.Add(outputName))
                    {
                        string suffix = Path.GetExtension(outputName);
                        string stem = outputName[..^suffix.Length];
                        int n = 1;
                        string candidate;
                        do
                        {
                            candidate = $"{stem}_{n++}{suffix}";
                        } while (!outputNames.Add(candidate));
                        outputName = candidate;
                    }

                    pages.Add((entry, outputName));
                }

                int total = pages.Count;
                int current = 0;

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
                (
                    ZipArchiveEntry Entry,
                    string OutputName,
                    string TempIn,
                    string TempOut,
                    Task Write
                )? pending = null;

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
                            p.OutputName,
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
                    foreach (var (entry, outputName) in pages)
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
                        // Index-only input name: a source entry's stem can contain characters the
                        // host OS cannot put in a filename ("a:b.jpg" is legal on Linux and not on
                        // Windows), and building that path would fail the chapter permanently. The
                        // extension is kept so the decoder still gets a format hint; it is always a
                        // supported image extension because the entry was filtered on it.
                        string tempInPage = Path.Combine(
                            tempDir,
                            $"in_{pageId}{Path.GetExtension(entry.Name)}"
                        );
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
                        pending = (entry, outputName, tempInPage, tempOutPage, write);
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

            // Overwrite in one step: deleting the destination first would leave a window in which the
            // chapter does not exist at all, and the move itself is atomic because the staged archive
            // is a sibling on the same volume.
            File.Move(tempOutCbz, outputCbzPath, overwrite: true);
        }
        finally
        {
            // Released whether the chapter succeeded, failed or was cancelled, exactly as the streamed
            // path does in its own finally. On the success-only path a chapter that failed (or timed
            // out) kept its session — and every buffer the provider grew for it — alive into its own
            // retry, and the next chapter planned its budget against memory the process did not
            // actually own. The flush is synchronous because the deferred InvalidateAllSessions drain
            // would still be waiting on a scope when the next chapter began. No inference scope is
            // held here (every page's inference and encode was awaited above), so taking the write
            // lock on this thread is safe.
            try
            {
                sessionFactory.ReleaseCachedSessions();
                GC.Collect();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown raced the chapter; the factory is already freeing what it owns.
            }

            try
            {
                // The staged archive is a sibling of the destination, so it is not inside tempDir.
                if (File.Exists(tempOutCbz))
                {
                    File.Delete(tempOutCbz);
                }

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
    /// <para>
    /// The file set is ordered by name, which puts the fp16 copy of a model before its fp32 one:
    /// the smaller file wins wherever it can run, and the fp32 copy is what the WebGPU EP falls
    /// back to for the architectures it cannot run in fp16.
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

        string[] onnxFiles = Directory
            .GetFiles(modelsDirectory, "*.onnx")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
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
                    foreach (
                        var file in versionCandidates
                            .Where(f =>
                                Path.GetFileName(f)
                                    .Contains(arch, StringComparison.OrdinalIgnoreCase)
                            )
                            .OrderBy(ArchRank)
                    )
                    {
                        AddCandidate(candidates, file);
                    }
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

    /// <summary>
    /// Order of two copies of the same architecture: the fp32 fallback comes last, because it is
    /// the larger file and is only needed where fp16 does not run. Within one precision, a file
    /// WebGPU cannot run natively comes after one it can — that is what the bf16 models need.
    /// </summary>
    private static int ArchRank(string path) =>
        (ModelFileNames.IsFp32(path) ? 2 : 0) + (NotBf16(path) ? 0 : 1);

    private static void AddCandidate(List<string> candidates, string file)
    {
        if (!candidates.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(file);
        }
    }

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

    private static void AddAll(List<string> candidates, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            AddCandidate(candidates, file);
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
        CancellationToken cancellationToken
    )
    {
        var unusable = new List<string>();
        Exception? firstTransientProbeFailure = null;

        foreach (string modelPath in candidates)
        {
            (bool usable, Exception? transientFailure) = await ModelUsableOnThisDeviceAsync(
                modelPath,
                scale,
                cancellationToken
            );
            if (!usable)
            {
                // Keep the first transient failure even when a later structural failure follows:
                // the transient one is the one a retry could get past, and losing its type is what
                // turns a recoverable resource hiccup into a permanent chapter failure upstream.
                firstTransientProbeFailure ??= transientFailure;
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
                    cancellationToken
                );
                return (modelPath, upscaled);
            }
            catch (NonFiniteModelOutputException ex)
            {
                MarkModelUnusable(modelPath, scale);
                unusable.Add(Path.GetFileName(modelPath));
                logger.LogWarning(
                    ex,
                    "Model {Model} produced non-finite output on device {Device} with the {Provider} execution provider, so it would have written a black page. Trying the next model.",
                    Path.GetFileName(modelPath),
                    deviceId,
                    sessionFactory.GetEffectiveBackend()
                );
            }
            catch (Exception ex) when (IsCandidateModelFailure(ex))
            {
                // The probe only runs a 256 px tile, so a model can pass it and still fail on the real
                // grid — a shape the EP's kernel does not implement, an output whose geometry does not
                // match the scale. That is the same "one bad file must not abort the page while a
                // working candidate follows" property the probe exists to provide, so it is handled
                // the same way: try the next candidate. A transient failure (memory, a lost device,
                // IO) is deliberately not caught here — it says nothing about the model, and it is
                // rethrown so the caller can retry the page.
                if (IsModelFault(ex))
                {
                    // Only a failure that is plausibly the model's becomes a permanent verdict. A
                    // programming error that happens to surface here skips the candidate — the page
                    // still falls through — but must not take a working model out of service for the
                    // rest of the process on the strength of a bug in our own code.
                    MarkModelUnusable(modelPath, scale);
                }

                unusable.Add(Path.GetFileName(modelPath));
                logger.LogWarning(
                    ex,
                    "Model {Model} failed on device {Device} with the {Provider} execution provider while upscaling; trying the next model.",
                    Path.GetFileName(modelPath),
                    deviceId,
                    sessionFactory.GetEffectiveBackend()
                );
            }
        }

        // A transient probe failure is rethrown with its original type so the caller (via
        // IsInfrastructureFailure and ClassifyStreamingFailure) can treat it as retryable instead of
        // a deterministic "no model can do this". Only a run where every candidate failed
        // structurally is genuinely a model-selection failure.
        if (firstTransientProbeFailure is not null)
        {
            throw firstTransientProbeFailure;
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
        CancellationToken cancellationToken
    )
    {
        // The per-pixel cost is a property of the model and the execution provider together, so the
        // benchmark is run for this model (once per device) instead of reusing a device-wide factor
        // that cannot represent the spread between an fp16 conv net and an fp32 transformer.
        DeviceMemoryProfile? profile = await deviceCalibrator.GetOrCalibrateForModelAsync(
            deviceId,
            ModelsDirectory,
            modelPath,
            scale,
            cancellationToken
        );

        return await Task.Run(
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

                // The execution provider caches its buffers per tensor shape, so a page at a shape
                // this session has not run yet would add a second set instead of reusing the first.
                // Release the session first; see _sessionShape for the measurement behind this.
                (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
                ReleaseSessionForShapeChange(modelPath, paddedWidth, paddedHeight);

                var (vramBefore, gttBefore) = OnnxTiler.GetGpuMemoryUsage(deviceId);
                logger.LogDebug(
                    "Upscaling {InputPath} ({Width}x{Height}, scale {Scale}) using model {Model} in {Columns}x{Rows} tiles of {TileWidth}x{TileHeight} (predicted peak: {PredictedMb} MB, budget: {BudgetMb} MB, VRAM: {VramMb} MB, GTT: {GttMb} MB)",
                    Path.GetFileName(inputPath),
                    width,
                    height,
                    scale,
                    Path.GetFileName(modelPath),
                    split.Columns,
                    split.Rows,
                    split.TileWidth,
                    split.TileHeight,
                    OnnxTiler.PredictTilePeakBytes(
                        split,
                        scale,
                        modelPath,
                        modelSizeBytes,
                        isFp16,
                        profile
                    ) / (1024 * 1024),
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
                        // Fetched per iteration, not once outside the loop: the memory-pressure path
                        // below invalidates the cached session, which retires the very instance this
                        // variable holds. InferenceSession has no disposed guard, so a Run on that
                        // handle surfaces as an opaque native error that is not a memory exception —
                        // it would land in the generic catch and lose the page instead of recovering
                        // at the halved tile. The lookup itself is a dictionary hit on the warm path.
                        // The scope is taken BEFORE the fetch so an eviction on another thread cannot
                        // free the session between the two statements; the drain that disposes evicted
                        // sessions takes the write lock and therefore waits for this scope to end.
                        using (sessionFactory.EnterInferenceScope())
                        {
                            session = sessionFactory.GetOrCreateSession(modelPath);
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
                        when (OnnxTiler.IsMemoryException(ex)
                            && (attempt.TileWidth > 16 || attempt.TileHeight > 16)
                        )
                    {
                        logger.LogWarning(
                            ex,
                            "Memory pressure encountered upscaling in {Columns}x{Rows} tiles of {TileWidth}x{TileHeight}. Halving tile size and recreating session.",
                            attempt.Columns,
                            attempt.Rows,
                            attempt.TileWidth,
                            attempt.TileHeight
                        );
                        // Synchronous flush, not InvalidateSession: the deferred drain waits for
                        // every read scope, and the halved retry takes a new one immediately, so the
                        // OOM-causing session would stay resident for the whole retry and double peak
                        // VRAM in the very path meant to reduce it. This catch runs after the scope
                        // above has been disposed (the using wraps the try body), so the caller holds
                        // no scope and the write lock cannot deadlock.
                        // This is also the only halving loop: the tiler used to halve internally on
                        // the same session, which hid the failure from here and kept the arena
                        // resident, so the two descended through the same sizes twice.
                        sessionFactory.ReleaseCachedSessions();
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
    }

    /// <summary>
    /// Releases the cached session when the next page needs a padded tensor shape this model has not
    /// run on it yet. See <see cref="_sessionShape"/> for why.
    /// </summary>
    private void ReleaseSessionForShapeChange(string modelPath, int paddedWidth, int paddedHeight)
    {
        bool changed;
        lock (_sessionShapeGate)
        {
            changed =
                string.Equals(_sessionShapeModel, modelPath, StringComparison.OrdinalIgnoreCase)
                && _sessionShape != (paddedWidth, paddedHeight);
            _sessionShapeModel = modelPath;
            _sessionShape = (paddedWidth, paddedHeight);
        }

        if (!changed)
        {
            return;
        }

        logger.LogDebug(
            "Tile shape for {Model} changed to {Width}x{Height}; releasing the cached session so the execution provider's per-shape buffer cache cannot accumulate across the chapter.",
            Path.GetFileName(modelPath),
            paddedWidth,
            paddedHeight
        );
        sessionFactory.ReleaseCachedSessions();
        GC.Collect();
    }

    /// <summary>
    /// Whether this model can run on the configured device at this scale, remembered for the
    /// process after the first answer.
    /// <para>
    /// The question is asked with one small tile through the real path, because there is no other
    /// way to know: an execution provider that does not implement a model's kernels does not say
    /// so at session creation, it returns NaN from the first inference. Running that check on a
    /// 64x64 tile costs milliseconds, while running it on the page costs the whole page.
    /// </para>
    /// </summary>
    private async Task<(bool Usable, Exception? TransientFailure)> ModelUsableOnThisDeviceAsync(
        string modelPath,
        int scale,
        CancellationToken cancellationToken
    )
    {
        var key = new ModelScaleKey(modelPath, scale);

        lock (_modelUsable)
        {
            if (_modelUsable.TryGetValue(key, out bool known))
            {
                return (known, null);
            }
        }

        bool usable;
        bool cacheVerdict = true;
        Exception? transientFailure = null;
        try
        {
            usable = await Task.Run(
                () =>
                {
                    // The scope is taken before the fetch so an eviction cannot dispose the session
                    // between the lookup and the Run below.
                    using (sessionFactory.EnterInferenceScope())
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
                    }

                    return true;
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NonFiniteModelOutputException ex)
        {
            // Structural: the provider ran the model but produced NaN/Inf, so the model itself
            // cannot serve this scale on this device. Cache the verdict.
            sessionFactory.InvalidateSession(modelPath);
            logger.LogWarning(
                ex,
                "Model {Model} produced non-finite output during the device probe; skipping it.",
                Path.GetFileName(modelPath)
            );
            usable = false;
        }
        catch (Exception ex) when (IsTransientProbeFailure(ex))
        {
            // Transient: a resource hiccup (out of memory, a lost device) or an IO error, not a
            // property of the model. Skip the model for this attempt but do NOT cache the verdict,
            // so a later page retries it once the resource is available again. The exception is
            // returned to the caller so that, when no candidate can be probed, its type survives
            // instead of being flattened into a permanent "no model works" error.
            sessionFactory.InvalidateSession(modelPath);
            logger.LogWarning(
                ex,
                "Model {Model} could not be probed because of a transient failure; it will be retried.",
                Path.GetFileName(modelPath)
            );
            usable = false;
            cacheVerdict = false;
            transientFailure = ex;
        }
        catch (Exception ex)
        {
            // Structural: a graph that does not expose "input"/"output", a corrupt or non-ONNX
            // file, an output at a different scale, or a provider that refuses it — the model
            // cannot serve this scale here. Candidate selection deliberately appends every .onnx as
            // a last resort, so a single bad file must be skipped rather than abort the page while
            // a working candidate follows.
            sessionFactory.InvalidateSession(modelPath);
            logger.LogDebug(
                ex,
                "Model {Model} could not be probed on this device; skipping it.",
                Path.GetFileName(modelPath)
            );
            usable = false;
        }

        if (cacheVerdict)
        {
            RecordUsable(key, usable);
        }

        return (usable, transientFailure);
    }

    /// <summary>
    /// True for a probe failure that says nothing about the model itself: a resource error (memory,
    /// a lost device) or an IO error. Such a failure must not become a permanent "unusable" verdict.
    /// </summary>
    private static bool IsTransientProbeFailure(Exception ex) =>
        OnnxTiler.IsMemoryException(ex) || ex is IOException;

    /// <summary>
    /// True for a real-tile failure that means the model cannot serve this scale on this device, as
    /// opposed to something a retry could get past. Mirrors how the probe classifies its own failures
    /// (<see cref="IsTransientProbeFailure"/>), so a model that passes the 256 px probe and then fails
    /// on the real grid is skipped rather than aborting the page while a working candidate follows.
    /// Cancellation and a disposed session factory are not the model's fault and are never treated as
    /// a model failure.
    /// </summary>
    internal static bool IsCandidateModelFailure(Exception ex) =>
        ex is not OperationCanceledException
        && ex is not ObjectDisposedException
        && !IsTransientProbeFailure(ex);

    /// <summary>
    /// True for a failure the execution provider reports for the model itself — a graph it cannot
    /// load, an operator or shape it does not implement, an output whose geometry does not match the
    /// requested scale. Only these become a permanent "unusable on this device" verdict; every other
    /// failure skips the candidate for this page but leaves the model usable, so a tiler bug cannot
    /// disable a model that works.
    /// </summary>
    private static bool IsModelFault(Exception ex) =>
        ex
            is OnnxRuntimeException
                or NonFiniteModelOutputException
                or ArgumentException
                or InvalidOperationException;

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

    private void MarkModelUnusable(string modelPath, int scale) =>
        RecordUsable(new ModelScaleKey(modelPath, scale), false);

    /// <summary>
    /// Stores a probe verdict, clearing the cache first when it is full so an unbounded set of
    /// unrelated <c>.onnx</c> files cannot grow it without limit.
    /// </summary>
    private void RecordUsable(ModelScaleKey key, bool usable)
    {
        lock (_modelUsable)
        {
            if (_modelUsable.Count >= ModelUsableCacheCapacity && !_modelUsable.ContainsKey(key))
            {
                _modelUsable.Clear();
            }

            _modelUsable[key] = usable;
        }
    }

    /// <summary>
    /// Rejects archive entry names that could escape the output archive on extraction (absolute or
    /// parent-traversing paths). Mirrors <c>PageStreamSpool.IsSafeEntryName</c> in the main app;
    /// that type is not reachable from this Shared assembly, so the guard is duplicated rather than
    /// shared. Only a component made of dots and spaces is rejected ("..", ".. ", "..."), and a
    /// colon is left alone: it is legal on Linux, and rejecting it silently dropped legitimate pages.
    /// </summary>
    private static bool IsSafeEntryName(string name) =>
        !string.IsNullOrEmpty(name)
        && !Path.IsPathRooted(name)
        && !name.StartsWith('/')
        && !name.StartsWith('\\')
        && !IsWindowsDriveForm(name)
        && !name.Split('/', '\\').Any(IsUnsafeSegment);

    /// <summary>
    /// True for a Windows absolute drive prefix ("C:\", "C:/"), which must not be interpreted as a
    /// drive path even when the host is Linux.
    /// </summary>
    private static bool IsWindowsDriveForm(string name) =>
        name.Length >= 3
        && char.IsAsciiLetter(name[0])
        && name[1] == ':'
        && (name[2] == '/' || name[2] == '\\');

    private static bool IsUnsafeSegment(string segment)
    {
        // Anything with a non-dot/space character is an ordinary name.
        if (segment.TrimEnd(' ', '.').Length > 0)
        {
            return false;
        }

        // Only dots and spaces: a parent traversal when it has at least two dots ("..", ".. ", "...").
        return segment.Count(c => c == '.') >= 2;
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
