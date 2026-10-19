using System.IO.Compression;
using System.Runtime.InteropServices;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
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
    /// Upper bound on the bytes decompressed out of a single archive entry when a CBZ is read or
    /// copied, so a crafted archive with a highly compressible entry cannot exhaust temp disk and,
    /// when libvips decodes a giant image, process memory. Re-exported from
    /// <see cref="ArchiveSizeLimits.MaxEntryBytes"/>, which the streamed path and <c>ImageController</c>
    /// reference directly, so the per-entry cap is stated once.
    /// </summary>
    public const long MaxArchiveEntryBytes = ArchiveSizeLimits.MaxEntryBytes;

    /// <summary>
    /// Fixed upper bound on the total decompressed bytes read from one source archive, so a chapter
    /// whose entries each pass <see cref="MaxArchiveEntryBytes"/> cannot fill the disk either. The
    /// page-streamed spool's <c>PageStreamSpool.MaxTaskBytes</c> shares the same default value
    /// but is operator-configurable, so this whole-CBZ bound is not tied to that setting.
    /// </summary>
    public const long MaxArchiveBytes = ArchiveSizeLimits.DefaultMaxArchiveBytes;

    /// <summary>
    /// Failures at one (model, scale) that must be observed before the process-lifetime "unusable"
    /// verdict is recorded. A single provider failure is not evidence about the model: a driver
    /// reset, a lost device or a stalled WebGPU/Dawn queue produces exactly the same
    /// <see cref="OnnxRuntimeException"/> once ("Non-zero status code returned…"), and condemning a
    /// working model on it is what took a whole chapter's models out of service for a transient
    /// event. A deterministic fault (a contract mismatch, a file ONNX Runtime cannot load) latches
    /// on its first observation and does not use this.
    /// </summary>
    private const int ModelFailureRecurrenceThreshold = 2;

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
    /// A <c>false</c> is a process-lifetime verdict, so it is only recorded for a deterministic
    /// fault or after <see cref="ModelFailureRecurrenceThreshold"/> failures — see
    /// <see cref="_modelFailureCount"/>.
    /// </summary>
    private readonly Dictionary<ModelScaleKey, bool> _modelUsable = new(
        ModelScaleKeyComparer.Instance
    );

    /// <summary>
    /// Consecutive failures at one (model, scale) that have not reached the permanent verdict yet,
    /// keyed like <see cref="_modelUsable"/> and guarded by the same lock. A failure only removes a
    /// model from service once it has been seen twice without an intervening success, so a one-off
    /// provider/driver error skips the candidate for the page but leaves the model usable. Cleared by
    /// a successful run (see <see cref="ClearModelFailure"/>), so two failures that straddle a healthy
    /// page are not read as a model that cannot run.
    /// </summary>
    private readonly Dictionary<ModelScaleKey, int> _modelFailureCount = new(
        ModelScaleKeyComparer.Instance
    );

    /// <summary>Path and scale a probe verdict applies to.</summary>
    private readonly record struct ModelScaleKey(string Path, int Scale);

    /// <summary>
    /// Compares keys on the path ordinally. Case-insensitive comparison would conflate two distinct
    /// model files on a case-sensitive filesystem — every path here comes from the same candidate
    /// string it is looked up with, so no normalisation is needed — and it would disagree with the
    /// session factory and the detector, which key ordinal.
    /// </summary>
    private sealed class ModelScaleKeyComparer : IEqualityComparer<ModelScaleKey>
    {
        public static readonly ModelScaleKeyComparer Instance = new();

        public bool Equals(ModelScaleKey x, ModelScaleKey y) =>
            x.Scale == y.Scale && string.Equals(x.Path, y.Path, StringComparison.Ordinal);

        public int GetHashCode(ModelScaleKey obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Path), obj.Scale);
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

    private readonly Lock _stickyPlanGate = new();

    /// <summary>
    /// The plan the last page was inferred with, so a run of pages of the same size keeps one plan —
    /// and therefore one session — instead of re-planning each of them against a budget that moves by a
    /// few megabytes as the engine's own arena grows.
    /// <para>
    /// Measured on an RTX 5070 Ti with CUDA, 1125x1600 pages, fp16 4x ESRGAN: two consecutive pages of
    /// the same size planned 1x3 of 1125x534 (predicted peak 8342 MB) and then 2x2 of 563x800, because
    /// the budget had fallen from 8407 MB to 8333 MB — 74 MB, under 1%. That flip changed the padded
    /// tensor shape, which released the cached session and rebuilt it: about 5 s of a chapter that
    /// otherwise runs 3.6 s per page, and the falls and climbs in the VRAM plot. Pages that differ by a
    /// few pixels are not affected either way — the padded shape rounds up to a multiple of 64
    /// (<see cref="OnnxTiler.UniformTileTensorShape"/>), so a run of them shares a shape already.
    /// </para>
    /// </summary>
    private (string Model, int Width, int Height, OnnxTiler.TileSplit Split) _stickyPlan;

    /// <summary>
    /// How far the sticky plan's predicted peak may exceed the current budget before the page is
    /// re-planned instead. The wobble this exists for measured under 1%; the bound is what stops a
    /// genuinely shrinking budget from being ignored, and the memory-pressure descent remains the
    /// backstop for anything the bound lets through.
    /// </summary>
    private const double StickyPlanBudgetTolerance = 0.05;

    /// <summary>
    /// Device memory the engine's own cached session holds once it has run a page, as the planner
    /// predicted it for the tile grid that ran.
    /// <para>
    /// This is the fallback discount, used when the driver cannot answer for this process. The
    /// discount the budget normally takes is the driver's per-process figure (see
    /// <see cref="OwnSessionFootprintBytes"/>), because a prediction describes one grid: after a
    /// shape change it drops to the new grid's cost while the previous grid's arena is still
    /// resident, and it cannot see the arena growing from page to page.
    /// </para>
    /// </summary>
    private long _ownSessionPeakBytes;

    /// <summary>
    /// Device memory this process's own work added across a page (vramAfter − vramBefore), kept at
    /// its largest; the second input to the fallback discount above.
    /// </summary>
    private long _ownSessionMeasuredDeltaBytes;

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
                // the device, and is only discovered when it returns valid output. The architecture
                // order inside that list is the configured one.
                IReadOnlyList<string> candidates = SelectModelCandidates(
                    ModelsDirectory,
                    isGrayscale,
                    origHeight,
                    scale,
                    ModelArchitecturePreference.Resolve(config.Value.PreferredModelArchitectures),
                    // The precision the operator asked for (or the backend's own answer when the
                    // setting is unset). Which copy runs is decided here, while EngineIdentity records
                    // that same precision: with fp16 switched off but the fp16 copy ranked first
                    // anyway, the engine would run fp16 while the identity said fp32 — contradicting
                    // the setting and letting two workers that agree on the identity disagree on the
                    // pixels.
                    config.Value.ResolveUseFp16(sessionFactory.GetEffectiveBackend())
                );

                // The alpha band is dropped, not composited. This is the same choice the detector
                // makes (SplitDetectionService) for the same reason: the reference keeps the colour
                // stored under a transparent pixel and simply discards the band, while libvips'
                // Flatten() composites onto black. The output is re-created as a 3-band image, so
                // alpha is gone either way — but flattening would bake that black into the RGB, and
                // the model would then upscale a black region where the page stored a colour.
                using var opaque = vipsImage.HasAlpha()
                    ? vipsImage.ExtractBand(0, vipsImage.Bands - 1)
                    : vipsImage.Copy();
                using var rgb =
                    opaque.Bands == 3 && opaque.Interpretation == NetVips.Enums.Interpretation.Srgb
                        ? opaque.Copy()
                        : opaque.Colourspace(NetVips.Enums.Interpretation.Srgb);
                using var ucharRgb =
                    rgb.Format == NetVips.Enums.BandFormat.Uchar
                        ? rgb.Copy()
                        : rgb.Cast(NetVips.Enums.BandFormat.Uchar);

                byte[] inputBytes = ucharRgb.WriteToMemory<byte>();

                cancellationToken.ThrowIfCancellationRequested();

                int deviceId = Math.Max(0, config.Value.SelectedDeviceIndex - 1);

                // The budget is read inside UpscaleWithModelAsync, after the first-use benchmark: that
                // benchmark and the model probe allocate device memory of their own, and a budget read
                // before them plans the page as if that memory were free.
                var (vramBefore, gttBefore) = ReadDeviceMemoryUsage(deviceId);
                // This process's own figure, from the same two points, so the discount below is what the
                // page cost *us* and not what the whole card did.
                long? processVramBefore = ReadProcessMemoryBytes(deviceId);

                (string model, byte[] upscaledBytes) = await UpscaleWithFirstWorkingModelAsync(
                    inputPath,
                    candidates,
                    inputBytes,
                    origWidth,
                    origHeight,
                    scale,
                    deviceId,
                    processVramBefore,
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
                var (vramAfter, gttAfter) = ReadDeviceMemoryUsage(deviceId);
                long? processVramAfter = ReadProcessMemoryBytes(deviceId);

                // What the device charged this process for the page — the arena, a benchmark's leftover
                // session, a retry's. The next page's budget discounts it, and the figure is kept at its
                // largest rather than reassigned: a page that reuses the session adds nothing, so a
                // reassignment would drop the discount back to the prediction on every reused page and
                // the budget would wobble again. See _ownSessionMeasuredDeltaBytes.
                //
                // The driver's per-process figure is preferred for the same reason the recording prefers
                // it: the system-wide one includes every other tenant, and this figure is kept at its
                // largest, so one page on which another process allocated would inflate the budget for
                // the rest of the process.
                lock (_sessionShapeGate)
                {
                    long delta =
                        processVramBefore is { } ownBefore && processVramAfter is { } ownAfter
                            ? ownAfter - ownBefore
                            : vramAfter - vramBefore;
                    _ownSessionMeasuredDeltaBytes = Math.Max(
                        _ownSessionMeasuredDeltaBytes,
                        Math.Max(0, delta)
                    );
                }

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
            // True luminance, not the red plane. A grayscale page can fall back to a colour model
            // (the ladder's last rung is an IllustrationJaNai model), and that model's three
            // channels then differ slightly: keeping band 0 turns any residual channel imbalance
            // into a brightness/tint error. Colourspace(Bw) computes the weighted luminance the
            // reference uses, so the imbalance is averaged away instead of surfacing as red.
            using var bwImg = srgbImg.Colourspace(NetVips.Enums.Interpretation.Bw);
            SaveImage(bwImg, outputPath, format, quality);
        }
        else
        {
            SaveImage(srgbImg, outputPath, format, quality);
        }
    }

    public Task UpscaleCbzAsync(
        string inputCbzPath,
        string outputCbzPath,
        int scale,
        CompressionFormat format,
        int? quality,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken
    ) =>
        RewriteCbzAsync(
            inputCbzPath,
            outputCbzPath,
            scale,
            format,
            quality,
            progress,
            MaxArchiveEntryBytes,
            MaxArchiveBytes,
            cancellationToken
        );

    /// <summary>
    /// The whole-CBZ implementation with the decompression caps as parameters.
    /// <see cref="UpscaleCbzAsync"/> passes the production constants; a test passes small values to
    /// exercise the caps against a crafted archive without generating a half-gigabyte entry.
    /// </summary>
    internal async Task RewriteCbzAsync(
        string inputCbzPath,
        string outputCbzPath,
        int scale,
        CompressionFormat format,
        int? quality,
        IProgress<UpscaleProgress>? progress,
        long maxEntryBytes,
        long maxArchiveBytes,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputCbzPath))
        {
            throw new FileNotFoundException($"Input CBZ not found: {inputCbzPath}", inputCbzPath);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"cbz_upscale_{Guid.NewGuid():N}");
        // Owner-only at creation: the per-page scratch holds the chapter's private page bytes.
        DirectoryPermissions.CreateDirectoryRestricted(tempDir, logger);

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

                // Total decompressed bytes read out of the source archive so far, for the per-archive
                // cap. Counted across both the page extraction and the non-image pass-through.
                long archiveBytes = 0;

                var entries = new List<ZipArchiveEntry>(inArchive.Entries.Count);
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in inArchive.Entries)
                {
                    // An entry name that escapes the archive (absolute or parent-traversing) must
                    // never be written into the output: Path.ChangeExtension/CreateEntry preserve
                    // it verbatim, so a reader extracting the result could be made to write outside
                    // its target. Directory entries (empty Name) carry no name to escape with and
                    // are skipped by the copy loop below. The streamed path applies the same guard
                    // via the shared ArchiveEntryNameGuard.
                    if (
                        string.IsNullOrEmpty(entry.Name)
                        || ArchiveEntryNameGuard.IsSafeEntryName(entry.FullName)
                    )
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
                    // Bounded like the page extraction: a crafted non-image entry is otherwise copied
                    // at whatever size its stream yields.
                    archiveBytes += ArchiveEntryCopier.CopyWithLimits(
                        srcStream,
                        dstStream,
                        maxEntryBytes,
                        maxArchiveBytes,
                        archiveBytes,
                        nonImg.FullName
                    );
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

                        // Bounded decompression: the entry's declared length is not trusted (it can
                        // be unavailable, and a crafted archive can misdeclare it), so the bytes that
                        // actually pass through are counted. An oversized page fails the whole
                        // chapter — the same deterministic failure the streamed path applies to a page
                        // over its cap — instead of filling temp disk or process memory.
                        using (Stream entryStream = entry.Open())
                        using (
                            FileStream pageFile = new(
                                tempInPage,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None
                            )
                        )
                        {
                            archiveBytes += ArchiveEntryCopier.CopyWithLimits(
                                entryStream,
                                pageFile,
                                maxEntryBytes,
                                maxArchiveBytes,
                                archiveBytes,
                                entry.FullName
                            );
                        }

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
    /// <para>
    /// <paramref name="architecturePreference"/> re-orders the architectures, most preferred first;
    /// it defaults to <see cref="ModelArchitecturePreference.Default"/> and is what
    /// <c>Upscaler:PreferredModelArchitectures</c> feeds. The model <em>version</em> order
    /// (V3detail, V3denoise, V2standard, V1) stays the outer one, so promoting an architecture
    /// re-ranks it within each version. A grayscale page has no version ladder, so the preference is
    /// the only architecture order it applies, over its resolution rungs. Every file in the
    /// directory is still somewhere in the result: a file whose architecture is not in the list is
    /// reached by the trailing additions.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SelectModelCandidates(
        string modelsDirectory,
        bool isGrayscale,
        int height,
        int scale,
        IReadOnlyList<ModelArchitecture>? architecturePreference = null,
        bool useFp16 = true
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

        // Which of two precision copies of the same architecture leads. The engine runs whichever file
        // it picks, so this is where UseFp16 is honoured for inference (the download step only decides
        // which copies exist).
        bool preferFp32 = !useFp16;

        IReadOnlyList<ModelArchitecture> preferredArchs =
            architecturePreference ?? ModelArchitecturePreference.Default;

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

            // The configured architecture preference leads, then the precision copy matching
            // UseFp16 (the same ArchRank the colour branch applies). The first level is what makes a
            // grayscale page honour Upscaler:PreferredModelArchitectures the way the colour branch and
            // EngineIdentity do: without it a pinned architecture was ignored here while it decided
            // the colour path, so the two kinds of page in one chapter could run models the operator
            // ranked differently. The second level keeps the precision choice consistent with UseFp16
            // — the raw ordinal order would run the fp16 copy (its name sorts first) while the colour
            // path and EngineIdentity recorded fp32.
            string[] rankedFiles = onnxFiles
                .OrderBy(f => ArchitecturePreferenceRank(f, preferredArchs))
                .ThenBy(f => ArchRank(f, preferFp32))
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            AddMatch(candidates, rankedFiles, $"{scalePrefix}MangaJaNai_{res}");
            AddMatch(candidates, rankedFiles, $"{scalePrefix}MangaJaNai_");

            // A grayscale page still upscales fine with an illustration model: that is a model
            // trained on colour, not a model that only accepts colour input.
            AddMatch(candidates, rankedFiles, $"{scalePrefix}IllustrationJaNai_");
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

            // Architectures ordered by cost on this engine's typical device; the order is the
            // configurable one (Upscaler:PreferredModelArchitectures), defaulting to FDAT_M, ESRGAN,
            // SPAN, FDAT_XL, DAT2, HAT_L. Each file resolves to exactly one architecture, so it is
            // added at most once here and a model the list does not name is still reached by the
            // AddAll below. ArchRank then orders an architecture's own copies: fp16 before fp32, and
            // a model the WebGPU EP cannot run natively (_bf16) last.
            foreach (var pref in preferredVersions)
            {
                // Ranked by the requested precision: the arch loop below filters within this order,
                // and the AddAll of files no preferred architecture claims appends the rest in it,
                // so both honor UseFp16 the way the per-arch ranking does.
                var versionCandidates = onnxFiles
                    .Where(f =>
                        Path.GetFileName(f).StartsWith(pref, StringComparison.OrdinalIgnoreCase)
                    )
                    .OrderBy(f => ArchRank(f, preferFp32))
                    .ToList();

                foreach (ModelArchitecture arch in preferredArchs)
                {
                    foreach (
                        var file in versionCandidates
                            .Where(f => OnnxTiler.DetectArchitecture(f) == arch)
                            .OrderBy(f => ArchRank(f, preferFp32))
                    )
                    {
                        AddCandidate(candidates, file);
                    }
                }

                AddAll(candidates, versionCandidates);
            }

            AddMatch(candidates, onnxFiles, colorKey);
        }

        // Last resort: anything else at this scale, then anything at all. Both prefer the
        // requested precision, then the file name, so the choice neither depends on the order the
        // file system happens to return nor quietly contradicts UseFp16.
        AddAll(
            candidates,
            onnxFiles
                .Where(f =>
                    Path.GetFileName(f).StartsWith(scalePrefix, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(f => ArchRank(f, preferFp32))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        );
        AddAll(
            candidates,
            onnxFiles
                .OrderBy(f => ArchRank(f, preferFp32))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        );

        return candidates;
    }

    private static bool NotBf16(string path) =>
        !Path.GetFileName(path).Contains("bf16", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Order of two copies of the same architecture. The copy matching the requested precision leads:
    /// with fp16 in use the fp32 copy comes last (it is the larger file, only needed where fp16 does
    /// not run), and with fp16 switched off the fp32 copy comes first instead — the operator asked for
    /// it, and <see cref="EngineIdentity"/> records that precision, so quietly running fp16 would
    /// contradict both. Within one precision, a file WebGPU cannot run natively comes after one it can,
    /// which is what the bf16 models need.
    /// </summary>
    private static int ArchRank(string path, bool preferFp32) =>
        (ModelFileNames.IsFp32(path) == preferFp32 ? 0 : 2) + (NotBf16(path) ? 0 : 1);

    /// <summary>
    /// Position of a file's architecture in <paramref name="preferredArchs"/>, so a page can order
    /// its candidates the way the operator ranked the architectures. An architecture the preference
    /// does not name sorts after every named one, which is how the colour branch also appends the
    /// files no preferred architecture claims.
    /// </summary>
    private static int ArchitecturePreferenceRank(
        string path,
        IReadOnlyList<ModelArchitecture> preferredArchs
    )
    {
        ModelArchitecture architecture = OnnxTiler.DetectArchitecture(path);
        for (int i = 0; i < preferredArchs.Count; i++)
        {
            if (preferredArchs[i] == architecture)
            {
                return i;
            }
        }

        return preferredArchs.Count;
    }

    private static void AddCandidate(List<string> candidates, string file)
    {
        // Ordinal: every caller passes the same full path string that the directory listing produced,
        // so a duplicate is the same string; comparing case-insensitively would drop a genuinely
        // distinct file (two names differing only in case) on a case-sensitive filesystem.
        if (!candidates.Contains(file, StringComparer.Ordinal))
        {
            candidates.Add(file);
        }
    }

    public static string SelectModel(
        string modelsDirectory,
        bool isGrayscale,
        int height,
        int scale,
        IReadOnlyList<ModelArchitecture>? architecturePreference = null,
        bool useFp16 = true
    ) =>
        SelectModelCandidates(
            modelsDirectory,
            isGrayscale,
            height,
            scale,
            architecturePreference,
            useFp16
        )[0];

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
                && !candidates.Contains(file, StringComparer.Ordinal)
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
        long? processVramBefore,
        CancellationToken cancellationToken
    )
    {
        var unusable = new List<string>();
        Exception? firstTransientProbeFailure = null;
        int nonFiniteCandidates = 0;

        foreach (string modelPath in candidates)
        {
            (bool usable, Exception? transientFailure) = await ModelUsableOnThisDeviceAsync(
                modelPath,
                scale,
                deviceId,
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
                    processVramBefore,
                    cancellationToken
                );
                // A success clears the recurrence counter, so two failures separated by an
                // otherwise healthy page are not read as a model that cannot run.
                ClearModelFailure(new ModelScaleKey(modelPath, scale));
                return (modelPath, upscaled);
            }
            catch (NonFiniteModelOutputException ex)
            {
                // Deliberately no permanent verdict here. Non-finite output is the transient failure
                // this type exists for — a driver reset or a mis-executing provider produces it once,
                // which is why it is classified retryable upstream. Recording it disabled every
                // candidate for the process, so the retry skipped them all and threw the generic
                // "already known to be unusable" InvalidOperationException: the first attempt was
                // transient, the second terminal, and the spool of already-upscaled pages was gone —
                // exactly what the rethrow below exists to prevent. The retry is bounded by the
                // server's soft-failure cap, so a provider that really cannot run the models still
                // ends in a visible failure.
                unusable.Add(Path.GetFileName(modelPath));
                nonFiniteCandidates++;
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
                    // Only a failure that is plausibly the model's is eligible for the permanent
                    // verdict, and even then MarkModelUnusable records it only for a deterministic
                    // fault or a repeated one: a single provider status (a driver reset, a stalled
                    // queue phrased as "Non-zero status code returned…") says as much about the
                    // timing as about the model. A programming error that happens to surface here
                    // skips the candidate — the page still falls through — but must not take a
                    // working model out of service for the rest of the process on the strength of a
                    // bug in our own code.
                    MarkModelUnusable(modelPath, scale, ex);
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

        if (nonFiniteCandidates > 0)
        {
            // Every candidate produced non-finite output, which is the same failure a single candidate
            // produces and is classified as retryable there (ClassifyStreamingFailure treats
            // NonFiniteModelOutputException as transient, deliberately: a driver reset can produce it
            // once). Reported as a plain InvalidOperationException it would be classified permanent, and
            // the spool — the pages that did upscale — would be dropped for what may have been a
            // one-off. The retry is bounded by the server's soft-failure cap, so a genuinely unusable
            // provider still ends in a visible failure.
            throw new NonFiniteModelOutputException(
                $"Every candidate model in {ModelsDirectory} produced non-finite output for a "
                    + $"{width}x{height} page at {scale}x on the {sessionFactory.GetEffectiveBackend()} "
                    + "execution provider. This is the provider not being able to run these models, not "
                    + "a property of the page."
            );
        }

        throw new InvalidOperationException(
            $"No model in {ModelsDirectory} can upscale {width}x{height} at {scale}x: "
                + (
                    unusable.Count > 0
                        ? $"{string.Join(", ", unusable)} failed on this device — a load, kernel or output-geometry fault, or non-finite output."
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
        long? processVramBefore,
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

        // Read after that benchmark, not before the page: the benchmark runs two inferences on their
        // own sessions, and the model probe below creates a third, and ONNX Runtime's arenas keep
        // their high-water mark — so the memory they leave is resident while this page runs. A budget
        // read before them plans the page as if it were free. Measured on an RX 9070 XT, 1125x1600
        // page, fp16 4x ESRGAN: the pre-benchmark figure planned 2x2 of 563x800 (predicted 10.5 GB)
        // and the page peaked at 15.2 GB of the 16.3 GB card, spilling 2.4 GB into GTT; the GTT safety
        // net answered by flushing the session, the memory did not come back, and the pages after that
        // planned against a nearly full card and fell below the 256 px quality floor.
        long ownSessionBytes = OwnSessionFootprintBytes(processVramBefore);
        long budget =
            config.Value.ResolvedMemoryBudgetBytes > 0
                ? config.Value.ResolvedMemoryBudgetBytes
                : ReadAutoVramBudget(deviceId, ownSessionBytes);
        logger.LogDebug(
            "Automatic VRAM budget {BudgetMb} MB for {InputPath}; {OwnMb} MB of it discounted as this engine's own cached session.",
            budget / (1024 * 1024),
            Path.GetFileName(inputPath),
            ownSessionBytes / (1024 * 1024)
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

                // Scoped like the inference path below: with MaxCachedSessions == 1 a concurrent job
                // could evict and dispose this session between the fetch and the metadata read, and
                // reading InputMetadata off a disposed session is a native call.
                bool isFp16;
                using (sessionFactory.EnterInferenceScope())
                {
                    InferenceSession probeSession = sessionFactory.GetOrCreateSession(modelPath);
                    isFp16 =
                        probeSession.InputMetadata.TryGetValue("input", out var inputMeta)
                        && inputMeta.ElementType == typeof(Float16);
                }

                OnnxTiler.TileSplit split =
                    config.Value.TileSize > 0
                        ? OnnxTiler.TileSplit.For(width, height, config.Value.TileSize)
                    : config.Value.TileSize < 0 ? OnnxTiler.TileSplit.For(width, height, 0)
                    : ReuseOrPlanTileSplit(
                        modelPath,
                        width,
                        height,
                        scale,
                        budget,
                        modelSizeBytes,
                        isFp16,
                        profile
                    );

                // The execution provider caches its buffers per tensor shape, so a page at a shape
                // this session has not run yet would add a second set instead of reusing the first.
                // The registration and release happen per attempt inside AcquireSessionForShape;
                // see _sessionShape for the measurement behind this.

                var (vramBefore, gttBefore) = ReadDeviceMemoryUsage(deviceId);
                // This process's own figure, taken at the same point, so the footprint recorded after
                // the page measures what the page cost *us* rather than the whole card.
                long? processVramBefore = ReadProcessMemoryBytes(deviceId);
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
                // Whether the attempt that ran built its session's tensor shape fresh. Only such a
                // page measures the shape's footprint: a page that reused a cached shape allocates
                // nothing, so whatever the device shows it gaining while it ran belongs to something
                // else. A descent is not fresh either — the halving path registers the halved shape
                // before it retries, so the attempt that finally runs finds its shape already
                // registered (see ReleaseAndRegisterSessionShape).
                bool shapeBuiltFresh = false;
                while (true)
                {
                    try
                    {
                        // Registration, flush and fetch are one critical section against other pages
                        // (see AcquireSessionForShape). The release is a write-lock operation that
                        // waits for every in-flight scope, so a caller that decided under the gate
                        // and released outside it could be interleaved with another page's fetch,
                        // which would then run on a session cached for the previous shape.
                        (
                            IDisposable sessionScope,
                            InferenceSession session,
                            bool shapeChanged
                        ) = AcquireSessionForShape(modelPath, attempt);
                        shapeBuiltFresh = shapeChanged;
                        using (sessionScope)
                        {
                            upscaledBytes = UpscaleViaTiler(
                                inputBytes,
                                width,
                                height,
                                scale,
                                session,
                                attempt,
                                cancellationToken
                            );
                        }
                        break;
                    }
                    catch (Exception ex)
                        when (OnnxTiler.IsMemoryException(ex)
                            && OnnxTiler.CanHalveWithinFloor(
                                width,
                                height,
                                attempt,
                                OnnxTiler.EmergencyTileFloor
                            )
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
                        try
                        {
                            attempt = OnnxTiler.HalveSplit(width, height, attempt);
                            // The flush and the registration are one critical section against other
                            // pages, exactly as in AcquireSessionForShape: releasing outside the gate
                            // would let another page register its own shape and fetch between them,
                            // and this registration would then overwrite that page's shape while its
                            // session stayed cached — the accumulation the gate exists to prevent.
                            // The next AcquireSessionForShape sees the halved shape already
                            // registered, so it does not flush a third time.
                            ReleaseAndRegisterSessionShape(modelPath, attempt);

                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                        catch (ObjectDisposedException)
                        {
                            // Shutdown raced the retry: the factory is already freeing what it owns, so
                            // there is nothing left to do here. The memory failure is what the caller's
                            // classification should describe, not the teardown artefact — surfaced
                            // as-is it would be neither transient nor restartable.
                            System
                                .Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex)
                                .Throw();
                        }
                    }
                    catch (Exception ex) when (OnnxTiler.IsMemoryException(ex))
                    {
                        // The descent has bottomed out: halving again would leave a tile below
                        // EmergencyTileFloor, where the model has no receptive-field context left and
                        // the page would be upscaled in postage stamps — saving nothing but the failure
                        // itself. Surfaced as the memory failure it is (the worker classifies that as
                        // retryable) with a line that says which floor stopped it, so the log explains
                        // the outcome rather than only reporting memory pressure.
                        logger.LogWarning(
                            ex,
                            "Memory pressure upscaling {InputPath} with {Model} persists at {Columns}x{Rows} tiles of {TileWidth}x{TileHeight}, which is the smallest this engine will run ({Floor}px floor); failing the page instead of descending further.",
                            Path.GetFileName(inputPath),
                            Path.GetFileName(modelPath),
                            attempt.Columns,
                            attempt.Rows,
                            attempt.TileWidth,
                            attempt.TileHeight,
                            OnnxTiler.EmergencyTileFloor
                        );
                        sessionFactory.InvalidateSession(modelPath);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A cancelled page says nothing about the model, and dropping the session
                        // would make the next page rebuild it for nothing. The same holds for this
                        // code's own configuration faults — a TileSize the tiler rejects, a blend
                        // region wider than a manual tile — the same types IsModelFault refuses to
                        // condemn a model for; invalidating here would tear down a healthy cached
                        // session (a ~300 ms rebuild) for every page until the config is fixed.
                        // Only a failure of the model's own execution can leave the session or its
                        // arena in a state a rebuild can change.
                        if (IsModelFault(ex))
                        {
                            sessionFactory.InvalidateSession(modelPath);
                        }

                        throw;
                    }
                }

                // The session has run this grid, so it now holds this grid's footprint until the shape
                // changes, and the next page's budget discounts it (see ReadAutoVramBudget). Stored
                // after the loop so it is the attempt that actually ran — a descent that halved arrives
                // here with the halved split.
                lock (_sessionShapeGate)
                {
                    _ownSessionPeakBytes = OnnxTiler.PredictTilePeakBytes(
                        attempt,
                        scale,
                        modelPath,
                        modelSizeBytes,
                        isFp16,
                        profile
                    );
                }

                RecordObservedTileFootprint(
                    deviceId,
                    modelPath,
                    scale,
                    attempt,
                    vramBefore,
                    processVramBefore,
                    shapeBuiltFresh
                );

                return upscaledBytes;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Records what this page's padded tile shape actually cost the device, so the next plan for that
    /// shape is sized from a measurement instead of the calibration's extrapolation from two small
    /// benchmark tiles (see <see cref="OnnxTiler.PredictTilePeakBytes"/>).
    /// <para>
    /// The figure is this process's own growth across the page, taken from the driver's per-process
    /// figure when it has one (<see cref="OnnxTiler.TryGetProcessMemoryBytes"/>) and from the
    /// system-wide reading otherwise. The difference matters: the system-wide figure counts every other
    /// tenant, so another process allocating or freeing while the page runs lands in the measurement —
    /// the same padded shape was measured at 8406 MB once and 9683 MB another time, 1.3 GB apart, and
    /// the lower one reached a plan. The per-process figure cannot do that.
    /// </para>
    /// <para>
    /// What it covers is that shape's own footprint: the model weights if this page uploaded them, the
    /// execution provider's buffers for the shape, and its arena. Whatever the process already held —
    /// the probe's and the benchmark's arenas, a previous shape's buffers — is inside the baseline and
    /// stays out of it, which is what the plan that baseline's budget sized also assumed.
    /// </para>
    /// <para>
    /// Only a page that built its session's tensor shape fresh measures this: a reused shape allocates
    /// nothing of its own, so whatever the device shows it gaining while it ran belongs to something
    /// else, and recording that would size the next plan from noise. A page that descended through
    /// halved shapes is not fresh either: the halving path registers the shape it retries at, so the
    /// attempt that finally runs finds it already registered (see
    /// <c>ReleaseAndRegisterSessionShape</c>). Nothing here is allowed to fail the page — the
    /// measurement is an optimisation for the next plan, and the calibrator logs and swallows a store it
    /// cannot write.
    /// </para>
    /// </summary>
    private void RecordObservedTileFootprint(
        int deviceId,
        string modelPath,
        int scale,
        OnnxTiler.TileSplit split,
        long vramBefore,
        long? processVramBefore,
        bool shapeBuiltFresh
    )
    {
        if (!shapeBuiltFresh)
        {
            return;
        }

        long growth =
            processVramBefore is { } ownBefore && ReadProcessMemoryBytes(deviceId) is { } ownAfter
                ? ownAfter - ownBefore
                : ReadDeviceMemoryUsage(deviceId).VramUsed - vramBefore;
        if (growth <= 0)
        {
            return;
        }

        (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
        deviceCalibrator.RecordObservedTilePeak(
            deviceId,
            ModelsDirectory,
            modelPath,
            scale,
            paddedWidth,
            paddedHeight,
            growth
        );
        logger.LogDebug(
            "Measured tile footprint for {Model} at {Width}x{Height}: {GrowthMb} MB; the next plan for this shape uses it instead of the analytic estimate.",
            Path.GetFileName(modelPath),
            paddedWidth,
            paddedHeight,
            growth / (1024 * 1024)
        );
    }

    /// <summary>
    /// The engine-to-tiler call, isolated as the one seam the memory-descent tests inject through. The
    /// OOM regression this descent exists for lived exactly at this boundary — the tiler used to
    /// swallow the failure and halve internally on the same session, hiding it from the engine — so
    /// the tests throw from here rather than only from the scope acquisition, and any wrapper that
    /// stops this call's exception from reaching the descent has to get past a test that watches
    /// this method fail.
    /// </summary>
    protected internal virtual byte[] UpscaleViaTiler(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        OnnxTiler.TileSplit split,
        CancellationToken cancellationToken
    ) =>
        OnnxTiler.UpscaleRgb(
            rgbBytes,
            width,
            height,
            scale,
            session,
            split,
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// The automatic VRAM budget one page is planned with, with the engine's own cached session
    /// discounted from it.
    /// <para>
    /// The device figure counts the memory this process already holds: ONNX Runtime's arenas keep
    /// their high-water mark, so right after a page the card still shows the memory that page's tiles
    /// peaked at, and the next page's budget would be read against a card that looked full. That is
    /// what made the plan walk — measured on an RX 9070 XT, 1125x1600 page, fp16 4x ESRGAN: page 1
    /// planned 2x2 of 563x800 (512 px context, predicted 10.5 GB), page 2's budget was read as the
    /// 1.3 GB the device had left and planned 5x7 of 225x229 — below the 256 px quality floor — and
    /// the pages after that oscillated between 512 and 375 px context with a session rebuild each.
    /// </para>
    /// <para>
    /// What is discounted is what the driver reports for this process, not the tile grid's predicted
    /// cost, so the figure stays right while the arena grows from page to page and while a shape
    /// change leaves the previous grid's arena behind. Measured on an RTX 5070 Ti with the 1600p
    /// ESRGAN model at 2x2 of 563x800: the process held 7712 MB on the second page and 8738 MB on the
    /// twenty-fourth while the other tenants stayed at ~3.0 GB throughout; the grid's predicted cost
    /// (7416 MB) froze, so up to 1.6 GB of the engine's own memory was charged to those tenants and
    /// the budget fell from 10211 to 9184 MB over one chapter with nothing else on the card changing.
    /// The same figure makes the release paths self-correcting: once a session is dropped the
    /// process's usage drops with it, so a stale discount cannot inflate the budget. An explicit
    /// <c>MemoryBudgetBytes</c> is the operator's own number for the whole session and is never
    /// discounted.
    /// </para>
    /// </summary>
    /// <param name="ownSessionBytes">
    /// The footprint to discount, from <see cref="OwnSessionFootprintBytes"/>.
    /// </param>
    protected internal virtual long ReadAutoVramBudget(int deviceId, long ownSessionBytes) =>
        OnnxTiler.GetAvailableVramBudget(
            deviceId,
            config.Value.VramUtilizationFraction,
            config.Value.ResolvedVramSafetyMarginBytes,
            config.Value.ResolvedVramExclusiveThresholdBytes,
            ownSessionBytes
        );

    /// <summary>
    /// The device memory to add back to the budget because this process already holds it: the
    /// driver's per-process figure, or — where the driver has none — the cost predicted for the grid
    /// that last ran, whichever of its two sources is larger.
    /// <para>
    /// The reading is taken just before the page (see <c>processVramBefore</c> in
    /// <see cref="UpscaleFileStagedAsync"/>), so it describes the same moment as the device's used
    /// figure the budget is derived from, and it costs no extra query.
    /// </para>
    /// </summary>
    private long OwnSessionFootprintBytes(long? measuredThisProcess)
    {
        // The driver's per-process figure is what the device's used figure charges *this* process, so
        // discounting it leaves exactly the other tenants' share — the quantity the budget is allowed
        // to ignore. It is also the only source that survives a shape change: the prediction describes
        // one grid, so after the plan changes it drops immediately while the previous grid's arena is
        // still resident.
        if (measuredThisProcess is { } measured && measured > 0)
        {
            return measured;
        }

        // No per-process figure (a Vulkan implementation without the extension's per-process answer,
        // an unavailable nvidia-smi): the grid's cost is the best available figure, as it always was.
        // Returning zero instead would shrink the budget rather than merely make it less precise.
        lock (_sessionShapeGate)
        {
            return Math.Max(_ownSessionPeakBytes, _ownSessionMeasuredDeltaBytes);
        }
    }

    /// <summary>
    /// Current device memory usage, isolated as a seam so the footprint the budget discounts can be
    /// pinned in a test without a device.
    /// </summary>
    protected internal virtual (long VramUsed, long GttUsed) ReadDeviceMemoryUsage(int deviceId) =>
        OnnxTiler.GetGpuMemoryUsage(deviceId);

    /// <summary>
    /// This process's own device memory as the driver reports it, isolated as a seam so the recording's
    /// source can be pinned without a device. Null when the driver exposes no per-process figure, in
    /// which case the recording falls back to the system-wide reading; see
    /// <see cref="OnnxTiler.TryGetProcessMemoryBytes"/> for why the per-process figure is the right one.
    /// </summary>
    protected internal virtual long? ReadProcessMemoryBytes(int deviceId) =>
        OnnxTiler.TryGetProcessMemoryBytes(deviceId);

    /// <summary>
    /// Registers the padded tensor shape the caller is about to run and returns the session to run it
    /// on, releasing the cached session first when that shape changed — or is still unknown, which the
    /// device probe's small tile leaves it as — so the execution provider's per-shape buffer cache
    /// cannot accumulate across a chapter. See <see cref="_sessionShape"/> for the measurement.
    /// <para>
    /// The shape gate is held across the decision, the release and the fetch. Releasing is a
    /// write-lock operation that waits for every in-flight inference scope to drain, so a caller that
    /// only took the gate for the decision and released after it could interleave with another page:
    /// that page's fetch would still find the previous shape's session cached and run on it, which is
    /// the accumulation this exists to prevent. Holding the gate through the fetch makes the whole
    /// registration/release/fetch sequence atomic, and the second page's release then waits for the
    /// first page's scope to end, so the session it retires is one the first page has finished with —
    /// no session ever carries two shapes. The cost is a brief serialisation of session acquisition.
    /// </para>
    /// <para>
    /// <see cref="ShapeChanged"/> is the decision itself, reported to the caller because it is also
    /// the only page that measures anything about the shape's footprint: a fresh shape allocates its
    /// buffers now, a reused one allocates nothing (see <c>RecordObservedTileFootprint</c>).
    /// </para>
    /// </summary>
    /// <summary>
    /// The sticky plan for <paramref name="modelPath"/> at this page size, or a fresh one when the last
    /// plan was for a different model or page size, or no longer fits the budget within
    /// <see cref="StickyPlanBudgetTolerance"/>. Internal so the reuse rule can be exercised without a
    /// device.
    /// </summary>
    internal OnnxTiler.TileSplit ReuseOrPlanTileSplit(
        string modelPath,
        int width,
        int height,
        int scale,
        long budget,
        long modelSizeBytes,
        bool isFp16,
        DeviceMemoryProfile? profile
    )
    {
        lock (_stickyPlanGate)
        {
            if (
                string.Equals(_stickyPlan.Model, modelPath, StringComparison.Ordinal)
                && _stickyPlan.Width == width
                && _stickyPlan.Height == height
            )
            {
                // Predicted now rather than remembered: the shape this plan runs may have been measured
                // since it was chosen, and PredictTilePeakBytes prefers that measurement to the analytic
                // estimate. Comparing the remembered figure against the budget would test a plan against
                // a cost it no longer has — the log showed exactly that, the sticky line reporting the
                // 3356 MB the plan was chosen with while the page itself reported the 4386 MB the shape
                // had since been measured at.
                long peak = OnnxTiler.PredictTilePeakBytes(
                    _stickyPlan.Split,
                    scale,
                    modelPath,
                    modelSizeBytes,
                    isFp16,
                    profile
                );
                if (peak <= (long)(budget * (1 + StickyPlanBudgetTolerance)))
                {
                    logger.LogDebug(
                        "Keeping the previous page's plan for {Model} at {Width}x{Height}: {Columns}x{Rows} tiles of {TileWidth}x{TileHeight} (predicted peak: {PeakMb} MB, budget: {BudgetMb} MB).",
                        Path.GetFileName(modelPath),
                        width,
                        height,
                        _stickyPlan.Split.Columns,
                        _stickyPlan.Split.Rows,
                        _stickyPlan.Split.TileWidth,
                        _stickyPlan.Split.TileHeight,
                        peak / (1024 * 1024),
                        budget / (1024 * 1024)
                    );
                    return _stickyPlan.Split;
                }
            }

            OnnxTiler.TileSplit planned = OnnxTiler.PlanTileSplit(
                width,
                height,
                scale,
                modelPath,
                budget,
                modelSizeBytes,
                isFp16,
                profile
            );
            _stickyPlan = (modelPath, width, height, planned);
            return planned;
        }
    }

    private (IDisposable Scope, InferenceSession Session, bool ShapeChanged) AcquireSessionForShape(
        string modelPath,
        OnnxTiler.TileSplit split
    )
    {
        lock (_sessionShapeGate)
        {
            bool shapeChanged = RegisterSessionShapeLocked(modelPath, split);
            if (shapeChanged)
            {
                (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
                logger.LogDebug(
                    "Tile shape for {Model} changed to {Width}x{Height}; releasing the cached session so the execution provider's per-shape buffer cache cannot accumulate across the chapter.",
                    Path.GetFileName(modelPath),
                    paddedWidth,
                    paddedHeight
                );
                sessionFactory.ReleaseCachedSessions();
                GC.Collect();
            }

            // Scope before fetch, so an eviction on another thread cannot free the session between
            // the two statements; the drain that disposes evicted sessions takes the write lock and
            // therefore waits for this scope to end.
            IDisposable scope = sessionFactory.EnterInferenceScope();
            try
            {
                InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);
                return (scope, session, shapeChanged);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Frees the cached session and registers <paramref name="split"/> as the shape the next fetch
    /// will run, as one critical section under <see cref="_sessionShapeGate"/>. Used by the
    /// memory-pressure descent, which has just decided to halve: the release is unconditional — the
    /// session being dropped is the one whose arena failed the allocation, and a rebuild is the
    /// point — unlike <see cref="AcquireSessionForShape"/>, which releases only when the shape it is
    /// about to run differs from the registered one. The registration is what keeps the descent's
    /// next <see cref="AcquireSessionForShape"/> from flushing again for a shape it just chose, and
    /// doing both under the gate is what keeps another page from registering its own shape and
    /// fetching between the release and this registration.
    /// </summary>
    private void ReleaseAndRegisterSessionShape(string modelPath, OnnxTiler.TileSplit split)
    {
        lock (_sessionShapeGate)
        {
            RegisterSessionShapeLocked(modelPath, split);
            sessionFactory.ReleaseCachedSessions();
        }
    }

    /// <summary>
    /// Registers the padded tensor shape <paramref name="split"/> will be inferred at and reports
    /// whether that differs from the shape the cached session was last used at (or the model changed,
    /// or no shape is known yet). The caller must hold <see cref="_sessionShapeGate"/>, which is what
    /// makes the decision, the release that follows and the fetch that follows both atomic against
    /// other pages.
    /// </summary>
    private bool RegisterSessionShapeLocked(string modelPath, OnnxTiler.TileSplit split)
    {
        (int paddedWidth, int paddedHeight) = OnnxTiler.UniformTileTensorShape(split);
        bool changed =
            _sessionShapeModel is null
            || !string.Equals(_sessionShapeModel, modelPath, StringComparison.Ordinal)
            || _sessionShape != (paddedWidth, paddedHeight);
        _sessionShapeModel = modelPath;
        _sessionShape = (paddedWidth, paddedHeight);
        return changed;
    }

    /// <summary>
    /// Whether this model can run on the configured device at this scale, remembered for the
    /// process after the first answer.
    /// <para>
    /// The question is asked with one small tile through the real path, because there is no other
    /// way to know: an execution provider that does not implement a model's kernels does not say
    /// so at session creation, it returns NaN from the first inference. Running that check on a
    /// <see cref="ModelProbeTilePixels"/> tile costs milliseconds, while running it on the page costs
    /// the whole page.
    /// </para>
    /// </summary>
    private async Task<(bool Usable, Exception? TransientFailure)> ModelUsableOnThisDeviceAsync(
        string modelPath,
        int scale,
        int deviceId,
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

        // What an earlier process already learned about this model on this device. The probe below
        // costs a 256 px inference whose device memory ONNX Runtime's WebGPU buffer cache never
        // returns — measured at ~4.9 GB resident for the rest of the process — so it is only run when
        // the answer is genuinely unknown. A driver change that invalidates it is caught by the
        // per-tile guard on the page itself, which reports the model fault and condemns the model
        // through RecordModelFailure.
        bool? remembered = deviceCalibrator.TryGetModelUsableVerdict(
            deviceId,
            ModelsDirectory,
            modelPath,
            scale
        );
        if (remembered is { } verdict)
        {
            logger.LogDebug(
                "Model {Model} was already probed on this device ({Verdict}); skipping the probe inference.",
                Path.GetFileName(modelPath),
                verdict ? "usable" : "unusable"
            );
            lock (_modelUsable)
            {
                _modelUsable[key] = verdict;
            }

            return (verdict, null);
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
        catch (ObjectDisposedException)
        {
            // Shutdown raced the probe: the singleton factory is already disposing, so
            // EnterInferenceScope/GetOrCreateSession throw ObjectDisposedException. That says
            // nothing about the model — the same reasoning IsCandidateModelFailure applies to the
            // real-tile path — so it must not fall into the structural catch below, which would
            // report the candidate unusable and then surface the generic permanent "no model can
            // upscale" InvalidOperationException, dropping the chapter's spool for a teardown
            // artefact. Rethrown as-is so the infrastructure classification upstream
            // (MangaJaNaiWorkerClient.IsInfrastructureFailure walks the chain for it) treats the
            // page as retryable rather than terminal.
            throw;
        }
        catch (NonFiniteModelOutputException ex)
        {
            // Transient, not structural: the provider ran the model but produced NaN/Inf, the
            // failure a driver reset or a mis-executing provider produces once — the reason the
            // type is classified retryable upstream. Caching it made the *first* attempt terminal
            // already: the candidate loop skips a cached-unusable model and then throws the generic
            // permanent "no model can upscale" error, so the probe path defeated the retry before
            // the real page was ever reached. The type is handed to the caller instead, so a probe
            // that only failed this way surfaces as the retryable failure it is.
            sessionFactory.InvalidateSession(modelPath);
            logger.LogWarning(
                ex,
                "Model {Model} produced non-finite output during the device probe; it will be retried.",
                Path.GetFileName(modelPath)
            );
            usable = false;
            cacheVerdict = false;
            transientFailure = ex;
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
            // a working candidate follows. The verdict is cached only once it is conclusive —
            // deterministic by construction, or a repeated failure (see RecordModelFailure) — so a
            // provider that refused the graph once is probed again rather than taken out for the
            // process, and a non-model fault (this code's own config error) records no verdict at
            // all. Skipping this attempt is unaffected either way.
            //
            // The session is invalidated only when the failure is the model's own, exactly as the
            // real-tile path does (see UpscaleWithModelAsync): a tiler config fault — a TileSize the
            // tiler rejects, an invalid blend region — is not something a rebuild changes, and
            // invalidating for it tore down a healthy cached session (~300 ms) once per candidate
            // per page until the config was fixed.
            if (IsModelFault(ex))
            {
                sessionFactory.InvalidateSession(modelPath);
            }

            bool permanentVerdict = IsModelFault(ex) && RecordModelFailure(key, ex);
            if (permanentVerdict)
            {
                // The verdict is cached for the process, so this failure is not transient and will not
                // be retried: an operator must see why a model dropped out of service.
                logger.LogWarning(
                    ex,
                    "Model {Model} cannot run on this device; it is unusable for this process and will be skipped.",
                    Path.GetFileName(modelPath)
                );
            }
            else
            {
                logger.LogDebug(
                    ex,
                    "Model {Model} could not be probed on this device; skipping it.",
                    Path.GetFileName(modelPath)
                );
            }

            usable = false;
            cacheVerdict = permanentVerdict;
        }

        if (cacheVerdict)
        {
            RecordUsable(key, usable);
            deviceCalibrator.SetModelUsableVerdict(
                deviceId,
                ModelsDirectory,
                modelPath,
                scale,
                usable
            );
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
    /// requested scale, an input it does not name the way the engine requires. Only such a failure can
    /// leave the session or its arena in a state a rebuild changes, so this is what invalidates the
    /// cached session (see <see cref="UpscaleWithModelAsync"/>).
    /// <para>
    /// It is also the eligibility gate for the process-lifetime "unusable on this device" verdict,
    /// not the verdict itself: <see cref="RecordModelFailure"/> records that verdict only for a
    /// deterministic fault or after a repeated failure. A failure this returns false for skips the
    /// candidate for this page but leaves the model usable, so a tiler bug or a misconfigured
    /// <c>TileSize</c> cannot disable a model that works.
    /// </para>
    /// </summary>
    internal static bool IsModelFault(Exception ex) =>
        ex
            is OnnxRuntimeException
                or NonFiniteModelOutputException
                or ModelContractMismatchException;

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

    /// <summary>
    /// Records a failure that <see cref="IsModelFault"/> attributed to the model and reports whether
    /// it has now become a process-lifetime unusable verdict, recording that verdict in
    /// <see cref="_modelUsable"/> when it has.
    /// <para>
    /// A deterministic fault — the engine's own contract mismatch, or a model file ONNX Runtime
    /// cannot load — latches on the first observation: no retry makes a corrupt graph load or gives a
    /// model a geometry it does not produce. Every other failure is counted and only the second
    /// consecutive one at the same (model, scale) condemns the model, because the first is
    /// indistinguishable from a driver reset or a stalled provider queue. The count is per key rather
    /// than global so a model that works at 2x is not taken out by a 4x probe.
    /// </para>
    /// </summary>
    private bool RecordModelFailure(ModelScaleKey key, Exception ex)
    {
        lock (_modelUsable)
        {
            if (IsDeterministicallyUnusable(ex))
            {
                _modelFailureCount.Remove(key);
                _modelUsable[key] = false;
                return true;
            }

            if (
                _modelFailureCount.Count >= ModelUsableCacheCapacity
                && !_modelFailureCount.ContainsKey(key)
            )
            {
                _modelFailureCount.Clear();
            }

            _modelFailureCount.TryGetValue(key, out int count);
            count++;
            _modelFailureCount[key] = count;
            if (count < ModelFailureRecurrenceThreshold)
            {
                return false;
            }

            _modelFailureCount.Remove(key);
            _modelUsable[key] = false;
            return true;
        }
    }

    /// <summary>
    /// Clears a model's failure count after it has actually upscaled a page, so a failure either
    /// side of the success does not accumulate into a process-lifetime verdict.
    /// </summary>
    private void ClearModelFailure(ModelScaleKey key)
    {
        lock (_modelUsable)
        {
            _modelFailureCount.Remove(key);
        }
    }

    /// <summary>
    /// True for a model failure that is deterministic by construction and so may condemn the model on
    /// a single observation: the engine's own contract mismatch (a geometry or element type the model
    /// cannot satisfy), and a file ONNX Runtime cannot load or parse. No retry makes a corrupt file
    /// load or a graph acquire the fixed input the engine requires.
    /// </summary>
    internal static bool IsDeterministicallyUnusable(Exception ex) =>
        ex is ModelContractMismatchException
        || (ex is OnnxRuntimeException onnx && IsModelLoadFailure(onnx.Message));

    /// <summary>
    /// The status text ONNX Runtime returns when it cannot load or parse the model file (a non-ONNX
    /// file, a truncated protobuf, a graph with no model). Deliberately narrow: a provider failure on
    /// the same exception type must stay subject to the recurrence rule.
    /// </summary>
    private static bool IsModelLoadFailure(string message) =>
        message.Contains("Load model", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Protobuf parsing", StringComparison.OrdinalIgnoreCase)
        || message.Contains("does not have a graph", StringComparison.OrdinalIgnoreCase);

    private void MarkModelUnusable(string modelPath, int scale, Exception ex) =>
        RecordModelFailure(new ModelScaleKey(modelPath, scale), ex);

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
