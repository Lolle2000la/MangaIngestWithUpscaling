using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

[RegisterScoped]
public class SplitDetectionService(
    IOnnxSessionFactory onnxSessionFactory,
    ILogger<SplitDetectionService> logger,
    IStringLocalizer<SplitDetectionService> localizer,
    IOptions<UpscalerConfig>? upscalerConfig = null
) : ISplitDetectionService
{
    /// <summary>
    ///     Version of the split-detection algorithm whose findings are stored per chapter. Bumped when
    ///     the detector changes shape or behaviour, so a chapter detected by the previous algorithm is
    ///     re-detected instead of being treated as current.
    ///     <para>
    ///     2 = the in-process ONNX page-break detector. It replaced the PyTorch peak-mask algorithm in
    ///     the ONNX migration without changing this constant, so chapters detected before the upgrade
    ///     carried 1 and would never have been re-detected.
    ///     </para>
    /// </summary>
    public const int CURRENT_DETECTOR_VERSION = 2;

    /// <summary>Tensor names the pinned detector export declares, validated once per session.</summary>
    internal const string ModelInputName = "input";

    internal const string ProbabilitiesOutputName = "probabilities";
    internal const string PeakMaskOutputName = "peak_mask";

    /// <summary>
    /// Width every page is resized to before the detector runs. The height stays dynamic (it is the
    /// resized page height, or a window's height), so a model that bakes a fixed input width is a
    /// different file than the pinned export: it would fail every Run with a shape error, which the
    /// contract check turns into a clear message instead. Kept here rather than as a local constant so
    /// the check and the resize cannot drift apart.
    /// </summary>
    private const int DetectorInputWidth = 768;

    /// <summary>
    /// Chunk used when no memory budget could be measured at all (a device with no VRAM reporting, or
    /// CPU inference), where "no budget" is not evidence of abundance. It is the reference's window and
    /// the model's training crop, and it bounds the footprint to one window rather than the page;
    /// anything smaller barely helps, because the context margin dominates a window's size.
    /// </summary>
    private const int DetectorDefaultChunkRows = 2048;

    /// <summary>
    /// Rows of context a window carries beyond the rows it is trusted for. Must be at least the model's
    /// vertical receptive field, which is 1423 rows: the dilated CNN stack reaches +-1023 (ten 3x3
    /// residual blocks with dilations 1, 2, 4, ... 512, plus the input convolution), the smoothing
    /// convolution after the sigmoid +-8, and the peak test's MaxPool +-400 (kernel 801, matching the
    /// calibrated peak_distance of 800). With at least that much context a window reproduces the
    /// whole-page result exactly — verified bit-identical against the shipped model — so this bounds
    /// memory without changing which rows are detected as peaks. 1536 is that bound rounded up.
    /// </summary>
    private const int DetectorWindowMargin = 1536;

    /// <summary>
    /// Fixed part of one detector inference's footprint, and the per-row cost of it, measured on the
    /// shipped model (RX 9070 XT, ORT 1.29, WebGPU) as the working-set growth of a page pass: 1513 MiB
    /// for 4000 resized rows and 5615 MiB for 15000, i.e. ~0.373 MiB per row plus ~21 MiB. These are
    /// used to decide whether the whole page fits the memory budget and, when it does not, how large a
    /// window is affordable. They are deliberately round numbers above the measurement: an estimate
    /// that is too low would pick a window that does not fit, and the cost of being too high is only a
    /// smaller window.
    /// </summary>
    private const long DetectorFixedBytes = 32L * 1024 * 1024;

    private const long DetectorBytesPerRow = 420_000;

    /// <summary>
    /// Smallest chunk worth splitting into. Below this the context alone dominates the work — every
    /// window carries <see cref="DetectorWindowMargin"/> rows on each side regardless — so a budget too
    /// small for this still gets this, and an overrun is left to the retry path rather than turned into
    /// hundreds of near-identical windows.
    /// </summary>
    private const int DetectorMinChunkRows = 512;

    /// <summary>
    /// Largest chunk (and number of windows) whose inference fits <paramref name="budgetBytes"/>.
    /// <para>
    /// One window, i.e. the whole page in a single pass, whenever the page fits: windowing exists only
    /// to keep a tall page inside the budget, and it is not free — the context rows are inferred again
    /// for every window, so a 2048-row chunk with the 1536-row margin costs about 2.5 times the rows
    /// the page has. The result does not depend on the choice (the margin is what makes a window
    /// exact), so the budget is free to buy speed.
    /// </para>
    /// </summary>
    internal static (int ChunkRows, int WindowCount) PlanDetectorWindows(
        int pageRows,
        long budgetBytes
    )
    {
        if (pageRows <= 0)
        {
            return (1, 0);
        }

        // No budget could be measured: neither the page nor a window has a known ceiling, so use the
        // bounded default rather than assuming the device is roomy (the tile planner reaches for its
        // quality floor in the same situation).
        if (budgetBytes <= 0)
        {
            int defaultChunk = Math.Min(pageRows, DetectorDefaultChunkRows);
            return (defaultChunk, (pageRows + defaultChunk - 1) / defaultChunk);
        }

        // Rows of *window* the budget affords, after the whole-page allowance the planner elsewhere
        // also applies to a pass that cannot be recovered from.
        long affordableWindowRows =
            (long)(budgetBytes * OnnxTiler.SinglePassSafetyFraction) - DetectorFixedBytes;
        long affordableRows =
            affordableWindowRows > 0 ? affordableWindowRows / DetectorBytesPerRow : 0;

        if (affordableRows >= pageRows)
        {
            return (pageRows, 1);
        }

        long chunk = affordableRows - (2L * DetectorWindowMargin);
        // A page shorter than the minimum chunk is its own only window: every window's context
        // clamps to the page's edges, so splitting such a page cannot make its inference any
        // smaller than the page. Clamping to the page instead would invert Math.Clamp's bounds
        // and throw for exactly those pages.
        int chunkRows =
            pageRows < DetectorMinChunkRows
                ? pageRows
                : (int)Math.Clamp(chunk, DetectorMinChunkRows, pageRows);
        return (chunkRows, (pageRows + chunkRows - 1) / chunkRows);
    }

    /// <summary>
    /// Upper bound on the known-failure cache. A detector that cannot run on a backend is a property
    /// of the model and the execution provider, so the number of distinct entries is tiny; the cap
    /// only stops a pathological stream of distinct model paths from growing it without limit.
    /// Overflow clears it and restarts, which costs at most one re-failure.
    /// </summary>
    private const int KnownFailureCacheCapacity = 64;

    /// <summary>
    /// Bound used for one page's detection when <see cref="UpscalerConfig.UpscaleTimeout"/> is unset
    /// or non-positive. Detection has no other watchdog of its own, so "disabled" is not honoured
    /// here the way the upscale paths honour it.
    /// </summary>
    private static readonly TimeSpan DefaultDetectionTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Detectors already known to fail structurally on a backend for this process, keyed by model
    /// path and resolved backend, so a graph the execution provider cannot run is not rebuilt, run
    /// and failed again for every page of every chapter. Only structural failures are recorded: a
    /// transient failure (memory, IO) is retried on the next page, and a hang is retried after the
    /// timeout. Static because the service is scoped and the verdict is a property of the process,
    /// mirroring <c>OnnxUpscaleEngine</c>'s model-usability cache. The error message is stored so a
    /// cache hit still reports why the detector is unusable.
    /// </summary>
    private static readonly Dictionary<DetectorBackendKey, string> KnownFailures = new(
        DetectorBackendKeyComparer.Instance
    );

    private static readonly Lock KnownFailuresGate = new();

    private GpuBackend? effectiveBackend;

    /// <summary>
    /// The accelerator backend the detector will run on, resolved once per instance. The session
    /// factory resolves it by probing the machine, so it is not called for every page.
    /// </summary>
    private GpuBackend EffectiveBackend =>
        effectiveBackend ??= onnxSessionFactory.GetEffectiveBackend();

    /// <summary>Model path, resolved accelerator backend, and content signature a verdict applies to.</summary>
    /// <param name="Signature">
    /// The model file's length and last-write time, mirroring <c>DeviceMemoryProfileStore</c>'s marker
    /// convention (<c>(0, -1)</c> for a missing file, <c>(0, -2)</c> for unreadable metadata). A verdict
    /// keyed on the path alone outlived the file it described: replacing a broken detector left it
    /// blacklisted for the rest of the process, with no way to clear it short of a restart.
    /// </param>
    private readonly record struct DetectorBackendKey(
        string ModelPath,
        GpuBackend Backend,
        (long Length, long LastWriteUtcTicks) Signature
    );

    /// <summary>
    /// Compares keys ordinally on the path, matching the session cache's comparer: on a
    /// case-sensitive filesystem two paths differing only in case are two different model files,
    /// and a case-insensitive verdict key would let a verdict recorded for one silence the other.
    /// </summary>
    private sealed class DetectorBackendKeyComparer : IEqualityComparer<DetectorBackendKey>
    {
        public static readonly DetectorBackendKeyComparer Instance = new();

        public bool Equals(DetectorBackendKey x, DetectorBackendKey y) =>
            x.Backend == y.Backend
            && string.Equals(x.ModelPath, y.ModelPath, StringComparison.Ordinal)
            && x.Signature == y.Signature;

        public int GetHashCode(DetectorBackendKey obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ModelPath),
                obj.Backend,
                obj.Signature.Length,
                obj.Signature.LastWriteUtcTicks
            );
    }

    /// <summary>The verdict key for a model file as it is on disk right now.</summary>
    private static DetectorBackendKey KeyFor(string modelPath, GpuBackend backend)
    {
        (long Length, long LastWriteUtcTicks) signature;
        try
        {
            var info = new FileInfo(modelPath);
            signature = info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0L, -1L);
        }
        catch (Exception)
        {
            // Unreadable metadata is still a distinct, stable key rather than one that matches a
            // readable file.
            signature = (0L, -2L);
        }

        return new DetectorBackendKey(modelPath, backend, signature);
    }

    private static bool TryGetKnownFailure(string modelPath, GpuBackend backend, out string error)
    {
        lock (KnownFailuresGate)
        {
            return KnownFailures.TryGetValue(KeyFor(modelPath, backend), out error!);
        }
    }

    /// <summary>
    /// True for a failure that is a property of the loaded model or of running it on this backend —
    /// the only verdicts the known-failure cache may remember, because its key is the model file's
    /// identity and its lifetime is the process. Mirrors the upscaler's <c>IsModelFault</c>: our own
    /// argument and invariant faults are excluded on purpose, because they say nothing about the
    /// model file a verdict would be recorded under.
    /// </summary>
    private static bool IsModelOrBackendFault(Exception ex) =>
        ex is ModelContractMismatchException or OnnxRuntimeException;

    private static void RecordKnownFailure(string modelPath, GpuBackend backend, string error)
    {
        lock (KnownFailuresGate)
        {
            var key = KeyFor(modelPath, backend);
            if (KnownFailures.Count >= KnownFailureCacheCapacity && !KnownFailures.ContainsKey(key))
            {
                KnownFailures.Clear();
            }

            KnownFailures[key] = error;
        }
    }

    /// <summary>
    /// True for a detection failure that must be rethrown rather than reported as a per-page error.
    /// <para>
    /// A recorded error reaches SplitProcessingService as a failed finding, and when every page of
    /// the chapter carries one it calls SetFailedAsync — the chapter is failed for good. That verdict
    /// is only acceptable for a failure that is a property of the page (a corrupt image, handled by
    /// the last catch) or of the detector as a whole (a graph the provider cannot run, which
    /// <see cref="RecordKnownFailure"/> remembers). This is everything else: a resource error
    /// (memory, a lost device), an IO error, and a non-finite output — the last because the code that
    /// throws it calls an all-NaN output the mark of a mis-executing execution provider, which a
    /// freshly built session can change, and with a single detector there is no second model to fall
    /// back to. Rethrown, the worker's IsInfrastructureFailure / ClassifyStreamingFailure path
    /// requeues the task with the spool intact, exactly as the upscaler path does.
    /// </para>
    /// </summary>
    private static bool IsRetryableDetectionFailure(Exception ex)
    {
        // The whole chain, not just the outermost exception — aligned with the upscaler's classifiers
        // (MangaJaNaiWorkerClient.IsInfrastructureFailure, ClassifyStreamingFailure), which walk it
        // for exactly these types: a mount that is briefly unavailable during detection should not
        // permanently fail the chapter when the same condition during upscaling is retryable, even
        // when it surfaces wrapped in another exception.
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (
                OnnxTiler.IsMemoryException(current)
                || current is IOException
                || current is UnauthorizedAccessException
                || current is NonFiniteModelOutputException
            )
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<SplitDetectionResult>> DetectSplitsAsync(
        string inputPath,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var results = new List<SplitDetectionResult>();

        if (File.Exists(inputPath))
        {
            results.Add(await DetectSingleImageAsync(inputPath, cancellationToken));
        }
        else if (Directory.Exists(inputPath))
        {
            var images = Directory
                .GetFiles(inputPath)
                .Where(f =>
                    ImageConstants.SupportedImageExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()
                    )
                )
                .OrderBy(f => f)
                .ToList();

            int total = images.Count;
            int current = 0;

            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(
                    new UpscaleProgress(
                        total,
                        current,
                        "Detecting Splits",
                        $"Processing {Path.GetFileName(image)}"
                    )
                );

                results.Add(await DetectSingleImageAsync(image, cancellationToken));
                current++;

                progress?.Report(
                    new UpscaleProgress(
                        total,
                        current,
                        "Detecting Splits",
                        $"Processed {Path.GetFileName(image)}"
                    )
                );
            }
        }
        else
        {
            throw new FileNotFoundException(localizer["Error_InputPathNotFound", inputPath]);
        }

        return results;
    }

    private async Task<SplitDetectionResult> DetectSingleImageAsync(
        string imagePath,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException(localizer["Error_InputPathNotFound", imagePath]);
        }

        string modelPath = SplitDetectionLayout.ResolveModelPath(
            upscalerConfig?.Value.ResolvedModelsDirectory
        );
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(localizer["Error_CheckpointNotFound", modelPath]);
        }

        GpuBackend backend = EffectiveBackend;
        if (TryGetKnownFailure(modelPath, backend, out string knownError))
        {
            // This detector already failed structurally on this backend in this process; rebuilding
            // the same session would only fail again, once per page of every chapter.
            logger.LogDebug(
                "Skipping split detection on {ImagePath}: the detector is known to be unusable on {Backend}.",
                imagePath,
                backend
            );
            return new SplitDetectionResult { ImagePath = imagePath, Error = knownError };
        }

        logger.LogInformation("Running in-process ONNX split detection on {ImagePath}", imagePath);

        // Only a failure that came out of the session itself should drop the cached session. The
        // image is decoded before any session is requested, so whether this was ever assigned is
        // what separates an inference failure from a decode failure of an unrelated, healthy model.
        InferenceSession? session = null;
        // Whether execution reached the model (session creation or Run) rather than failing while
        // decoding the page. Only a model-path failure can be a property of the detector and cached.
        bool reachedModel = false;

        // Handed to Run so a timeout can actually abort the native call rather than only abandoning
        // the await. Kept alive (not disposed) if the run is abandoned, because ORT is still reading
        // it; it is then released by its finalizer once the native call returns.
        var runOptions = new RunOptions();
        // The shrinkage key belongs to RunOptions, not SessionOptions (see
        // OnnxSessionFactory.CreateSession): the detector runs a different image shape on every page, so
        // its arena would otherwise stay at the high-water mark of the largest page it has seen. This is
        // the one place in the engine that passes a RunOptions anyway, so it costs nothing.
        runOptions.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0;gpu:0");
        bool disposeRunOptions = true;
        // Declared out here so the cancellation path can tell whether the abandoned task is still
        // running: the options may only be freed once the native call has returned (see
        // RunIsStillGoing). A cancellation that lands before the model is reached leaves this null.
        Task<SplitDetectionResult>? detection = null;

        try
        {
            (int pageWidth, int pageHeight) = ReadImageSize(imagePath);
            detection = Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var vipsImage = NetVips.Image.NewFromFile(
                        imagePath,
                        access: NetVips.Enums.Access.Random
                    );
                    int origWidth = vipsImage.Width;
                    int origHeight = vipsImage.Height;

                    const int targetWidth = DetectorInputWidth;
                    double scale = (double)targetWidth / origWidth;
                    int resizedHeight = Math.Max(1, (int)(origHeight * scale));
                    double hscale = (double)targetWidth / origWidth;
                    double vscale = (double)resizedHeight / origHeight;

                    using var resized = vipsImage.Resize(
                        hscale,
                        vscale: vscale,
                        kernel: NetVips.Enums.Kernel.Linear
                    );
                    int actualWidth = resized.Width;
                    int actualHeight = resized.Height;

                    // The alpha band is dropped, not composited. That is what the reference does: PIL's
                    // convert("RGB") keeps the colour stored under a transparent pixel and simply
                    // discards the band, while libvips' Flatten() composites onto black. A transparent
                    // region therefore used to become a black band, and because the detector's
                    // probability is a row mean, that moved rows across the peak threshold — an image
                    // whose page area is transparent under the alpha was detected with a different
                    // split set, and a fully transparent page as no splits at all.
                    using var opaque = resized.HasAlpha()
                        ? resized.ExtractBand(0, resized.Bands - 1)
                        : resized.Copy();
                    using var rgb =
                        opaque.Bands == 3
                        && opaque.Interpretation == NetVips.Enums.Interpretation.Srgb
                            ? opaque.Copy()
                            : opaque.Colourspace(NetVips.Enums.Interpretation.Srgb);
                    using var ucharRgb =
                        rgb.Format == NetVips.Enums.BandFormat.Uchar
                            ? rgb.Copy()
                            : rgb.Cast(NetVips.Enums.BandFormat.Uchar);

                    // The packing loop below strides three bytes per pixel, so anything the
                    // conversion above did not normalize to three bands would be silently
                    // mis-read into the tensor — mixed channels rather than an error. libvips
                    // converts every standard interpretation to 3-band sRGB, so this is a guard
                    // against the unexpected rather than an expected case; it fires before the
                    // model is reached, so it is reported as a per-page error and does not
                    // poison the known-failure cache for the model.
                    if (ucharRgb.Bands != 3)
                    {
                        throw new InvalidOperationException(
                            $"The page converted to {ucharRgb.Bands} bands instead of the 3 the "
                                + $"detector packs; it is not a format the detector can read."
                        );
                    }

                    byte[] mem = ucharRgb.WriteToMemory<byte>();
                    int planeSize = actualHeight * actualWidth;
                    float[] tensorData = new float[1 * 3 * actualHeight * actualWidth];
                    int rOffset = 0;
                    int gOffset = planeSize;
                    int bOffset = planeSize * 2;

                    for (int i = 0; i < planeSize; i++)
                    {
                        int bIdx = i * 3;
                        tensorData[rOffset + i] = mem[bIdx] / 255.0f;
                        tensorData[gOffset + i] = mem[bIdx + 1] / 255.0f;
                        tensorData[bOffset + i] = mem[bIdx + 2] / 255.0f;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // The session is fetched before the scope is entered, so a concurrent
                    // invalidation/eviction in that narrow window could still dispose it. The scope
                    // does enclose the Run below, which is the part that must not run on a session
                    // another thread is disposing. The local is non-null for the compiler and for
                    // the Run; the captured field is what the catch reads to decide whether the
                    // failure came out of the session.
                    // The scope is taken before the fetch: an eviction on another thread otherwise
                    // finds no reader yet and can dispose the session between the lookup and the Run.
                    reachedModel = true;
                    using var inferenceScope = onnxSessionFactory.EnterInferenceScope();

                    InferenceSession localSession = onnxSessionFactory.GetOrCreateSession(
                        modelPath
                    );
                    session = localSession;

                    // Cheap dictionary lookups on the already-cached session, so this costs nothing per
                    // page; it is what turns a foreign file at the model path into a clear message
                    // instead of ORT's "is not in the metadata" or a bare NullReferenceException out of
                    // AsTensor.
                    RequireDetectorContract(localSession);

                    // Windows only as far as the memory budget requires, and one whole-page pass when the
                    // page fits. Windowing is what bounds the footprint of a tall page — a whole-page
                    // pass needs ~1.5 GiB at 4000 resized rows and ~5.6 GiB at 15000 — but it is not
                    // free: every window re-infers its DetectorWindowMargin rows of context on each
                    // side, so a 2048-row chunk costs about 2.5 times the page's rows. The margin (not
                    // the chunk) is what makes a window exact, so the chunk only trades memory against
                    // speed and the budget is free to buy the faster whole-page pass.
                    int deviceId = Math.Max(0, upscalerConfig?.Value.SelectedDeviceIndex - 1 ?? 0);
                    long budget;
                    if ((upscalerConfig?.Value.ResolvedMemoryBudgetBytes ?? 0) > 0)
                    {
                        budget = upscalerConfig!.Value.ResolvedMemoryBudgetBytes;
                    }
                    else if (backend == GpuBackend.CPU)
                    {
                        // On the CPU the budget helper's VRAM-shaped floor says nothing about the host
                        // RAM the pass actually lives in, and the floor can never report "no budget
                        // measurable", so it would buy a whole-page pass in host RAM for a tall page —
                        // exactly what the bounded default chunk exists to prevent. Treat the CPU as
                        // the unmeasurable case it is; an explicit MemoryBudgetBytes above still wins.
                        budget = 0;
                    }
                    else
                    {
                        budget = OnnxTiler.GetAvailableVramBudget(
                            deviceId,
                            upscalerConfig?.Value.VramUtilizationFraction,
                            upscalerConfig?.Value.ResolvedVramSafetyMarginBytes,
                            upscalerConfig?.Value.ResolvedVramExclusiveThresholdBytes
                        );
                    }
                    (int plannedChunkRows, int windowCount) = PlanDetectorWindows(
                        actualHeight,
                        budget
                    );
                    logger.LogDebug(
                        "Detector: {Rows} resized rows in {Windows} window(s) of {ChunkRows} rows (budget {BudgetMiB} MiB).",
                        actualHeight,
                        windowCount,
                        plannedChunkRows,
                        budget / 1024 / 1024
                    );

                    var probs = new float[actualHeight];
                    var peakMask = new bool[actualHeight];

                    // The plan is derived from a measured budget, not a guaranteed one: another
                    // process can take VRAM between the measurement and the run. Without a descent an
                    // over-committed page fails identically on every retry — the next attempt measures
                    // the same budget and re-plans the same windows — and can never succeed. Each step
                    // halves the chunk (the margin stays, so the window shrinks toward
                    // margin + floor) and re-runs the pass from a clean probs/peakMask; the floor
                    // bounds it at the size below which the margin dominates the work entirely, and
                    // past it the failure is rethrown for the worker's requeue path.
                    int chunkRows = plannedChunkRows;
                    while (true)
                    {
                        try
                        {
                            for (
                                int windowStart = 0;
                                windowStart < actualHeight;
                                windowStart += chunkRows
                            )
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                int windowEnd = Math.Min(windowStart + chunkRows, actualHeight);
                                int inputStart = Math.Max(0, windowStart - DetectorWindowMargin);
                                int inputEnd = Math.Min(
                                    actualHeight,
                                    windowEnd + DetectorWindowMargin
                                );
                                int windowHeight = inputEnd - inputStart;

                                // The three planes of tensorData are contiguous, so a window is one BlockCopy
                                // per plane. Rows outside the window are simply not fed. A window clamped to
                                // the page's own edge ends there, so the graph pads exactly as it does in a
                                // whole-page pass — the margin is only needed away from the page's edges.
                                var windowData = new float[3 * windowHeight * actualWidth];
                                for (int plane = 0; plane < 3; plane++)
                                {
                                    Buffer.BlockCopy(
                                        tensorData,
                                        sizeof(float)
                                            * ((plane * planeSize) + (inputStart * actualWidth)),
                                        windowData,
                                        sizeof(float) * (plane * windowHeight * actualWidth),
                                        sizeof(float) * windowHeight * actualWidth
                                    );
                                }

                                var windowTensor = new DenseTensor<float>(
                                    windowData,
                                    [1, 3, windowHeight, actualWidth]
                                );
                                var inputs = new[]
                                {
                                    NamedOnnxValue.CreateFromTensor(ModelInputName, windowTensor),
                                };

                                // The two outputs are named explicitly rather than passing null for "all":
                                // with a RunOptions the null-outputNames overload does not hand back every
                                // output, and these are the only two this reads.
                                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> windowOutputs =
                                    localSession.Run(
                                        inputs,
                                        new[] { PeakMaskOutputName, ProbabilitiesOutputName },
                                        runOptions
                                    );

                                var windowPeakMask = windowOutputs
                                    .First(o => o.Name == PeakMaskOutputName)
                                    .AsTensor<bool>();
                                var windowProbs =
                                    (DenseTensor<float>)
                                        windowOutputs
                                            .First(o => o.Name == ProbabilitiesOutputName)
                                            .AsTensor<float>();

                                // A mis-executing execution provider can return an all-NaN output instead of
                                // failing (the same failure class OnnxTiler rejects for the upscaler). Every
                                // comparison against NaN is false, so without this the mask is all-false and
                                // the page is reported as a successful detection with zero splits — a silent
                                // miss.
                                if (OnnxTiler.ContainsNonFinite(windowProbs.Buffer.Span))
                                {
                                    throw new NonFiniteModelOutputException();
                                }

                                // Both outputs must be one score per row of the window. A longer output would
                                // be silently truncated below and a shorter one read past its end, so an
                                // unexpected geometry is rejected rather than quietly mis-detected.
                                EnsureDetectorOutputGeometry(
                                    windowPeakMask,
                                    windowProbs,
                                    windowHeight
                                );

                                // Each window is trusted for its own rows only, which are the rows the margin
                                // exists to make exact.
                                int offset = windowStart - inputStart;
                                for (int y = windowStart; y < windowEnd; y++)
                                {
                                    int index = offset + (y - windowStart);
                                    probs[y] = windowProbs[0, index];
                                    peakMask[y] = windowPeakMask[0, index];
                                }
                            }

                            break;
                        }
                        catch (Exception ex)
                            when (OnnxTiler.IsMemoryException(ex)
                                && chunkRows > DetectorMinChunkRows
                            )
                        {
                            logger.LogWarning(
                                ex,
                                "Detector: memory pressure at {ChunkRows}-row windows for {ImagePath}; halving the window and retrying.",
                                chunkRows,
                                imagePath
                            );
                            chunkRows = Math.Max(DetectorMinChunkRows, chunkRows / 2);
                            Array.Clear(probs, 0, actualHeight);
                            Array.Clear(peakMask, 0, actualHeight);
                        }
                    }

                    const int edgeMargin = 100;
                    bool applyEdgeMargin = edgeMargin > 0 && actualHeight > edgeMargin * 2;
                    var splits = new List<DetectedSplit>();

                    for (int y = 0; y < actualHeight; y++)
                    {
                        if (applyEdgeMargin && (y < edgeMargin || y >= actualHeight - edgeMargin))
                        {
                            continue;
                        }

                        if (peakMask[y])
                        {
                            int yOrig = MapRowToOriginal(y, vscale, origHeight);
                            splits.Add(
                                new DetectedSplit
                                {
                                    YOriginal = yOrig,
                                    Confidence = Math.Round(probs[y], 4),
                                }
                            );
                        }
                    }

                    return new SplitDetectionResult
                    {
                        ImagePath = imagePath,
                        OriginalHeight = origHeight,
                        OriginalWidth = origWidth,
                        Splits = splits,
                        Count = splits.Count,
                    };
                },
                cancellationToken
            );

            // Run is a synchronous native call with no cancellation token, so the bound is on the
            // await, and the timeout handler below additionally asks ORT to stop through its RunOptions
            // (see the catch). WaitAsync on its own would only abandon the task: the native call would
            // keep running on its thread, and the process could not reclaim it. The upscale path is
            // bounded by the worker's inactivity watchdog; the detection path has none of its own.
            ObserveIfAbandoned(detection);
            return await detection.WaitAsync(
                ResolveDetectionTimeout(pageWidth, pageHeight),
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // The await was cancelled while the native Run may still be going, so the options are
            // still in use: freeing them here would pull the handle out from under ORT, the same
            // hazard the timeout path documents. Only a run that has already returned can release
            // them. Ask ORT to stop as well, so a cancelled page does not leave a full inference
            // running behind the abandoned await.
            if (RunIsStillGoing(detection))
            {
                try
                {
                    runOptions.Terminate = true;
                }
                catch (ObjectDisposedException)
                {
                    // The run finished and released the options in the meantime.
                }

                disposeRunOptions = false;
            }

            throw;
        }
        catch (TimeoutException ex)
        {
            // A hang says nothing about the detector's graph, so it is not cached: the next page
            // rebuilds the session and retries, matching how the worker treats a timed-out upscale
            // as recoverable. The abandoned Run may still hold the inference scope, so a retired
            // session is not disposed until that native call returns (see OnnxSessionFactory).
            // The actual abort. RunOptions.Terminate makes ORT stop at the next operator boundary, so a
            // run that is merely taking long is genuinely stopped rather than left running behind an
            // abandoned await. It cannot unstick a driver wedged inside a kernel — nothing in the
            // managed API can — but it is the strongest stop the API offers, and it is what makes this
            // a real bound instead of only a caller-side one.
            try
            {
                runOptions.Terminate = true;
            }
            catch (ObjectDisposedException)
            {
                // The run already finished and released the options; nothing left to stop.
            }

            // Owned by the still-running call now; freeing it here would pull the handle out from
            // under ORT. It is left to the finalizer when the run has not returned.
            disposeRunOptions = !RunIsStillGoing(detection);

            logger.LogError(ex, "ONNX split detection timed out for {ImagePath}", imagePath);
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            // Thrown as a TimeoutException rather than reported as a per-page error, for the same
            // reason as the transient catch above: an error finding ends in SetFailedAsync, while a
            // timeout is classified transient (the upscaler path treats a timed-out upscale the same
            // way) and requeues the task with the spool intact. It is also not cached as a detector
            // failure, so the retry re-probes.
            throw new TimeoutException(
                $"Split detection for {imagePath} timed out; the execution provider did not return.",
                ex
            );
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced this page: the session factory is being torn down. It is not a property
            // of the detector, and it must not become a page error — that reaches SetFailedAsync and
            // fails a chapter that is merely being stopped. Rethrown like a cancellation, without
            // touching the session or the known-failure cache.
            throw;
        }
        catch (Exception ex) when (IsRetryableDetectionFailure(ex))
        {
            // Not a property of the page. Deliberately not gated on having reached the model: the
            // vips decode and the float[1*3*H*W] tensor it feeds happen before the session is fetched,
            // and an OOM there is just as recoverable as one inside Run. Gated, it fell through to the
            // generic catch and was reported as a per-page error. Drop the session for the retry when
            // there is one.
            logger.LogWarning(
                ex,
                "Split detection for {ImagePath} failed recoverably; the task will be retried.",
                imagePath
            );
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            // Rethrown with its original type, not reported as a per-page error: the worker's
            // infrastructure classifier recognises it as transient and requeues the task with the
            // spool intact, which is exactly what the same machinery does on the upscaler path.
            throw;
        }
        catch (Exception ex) when (reachedModel && IsModelOrBackendFault(ex))
        {
            logger.LogError(ex, "Error running ONNX split detection for {ImagePath}", imagePath);

            // Only one session is cached at a time (OnnxSessionFactory.MaxCachedSessions), so a
            // session that keeps failing would be reused, and fail again, for every remaining page
            // of the chapter. Drop it so the next page rebuilds a fresh one, and remember the
            // structural failure for this backend so the rebuild does not happen on every page of
            // every chapter either.
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            RecordKnownFailure(modelPath, backend, ex.Message);
            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
        }
        catch (Exception ex) when (reachedModel)
        {
            // The model was reached, but the failure is not a property of it or of the backend: it
            // is this code's own setup around the model — an argument or invariant fault, the same
            // types the upscaler's IsModelFault explicitly refuses to condemn a model for. Recording
            // it as a model verdict would permanently fail detection for every later chapter under
            // the current model file (a planner bug would outlive the budget that triggered it), so
            // it is reported per page instead: the chapter fails, the next one retries, and the
            // healthy session stays cached.
            logger.LogError(ex, "Error running ONNX split detection for {ImagePath}", imagePath);
            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
        }
        catch (Exception ex)
        {
            // Failed before reaching the model (a corrupt or unsupported page). The detector is not
            // at fault, so it must not be invalidated or marked unusable.
            logger.LogError(ex, "Error running ONNX split detection for {ImagePath}", imagePath);
            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
        }
        finally
        {
            // Freed only once the run has returned. On the timeout path the native call is still
            // using it, so it is deliberately left to the finalizer instead (see the catch).
            if (disposeRunOptions)
            {
                runOptions.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether the native <c>Run</c> is still executing on the abandoned detection task. The
    /// <see cref="RunOptions"/> handed to it may only be freed once this is false: it is a
    /// <c>SafeHandle</c>, and ORT reads it for the whole duration of the call, so disposing it early
    /// is a use-after-free (and asking it to <c>Terminate</c> then touches a freed handle). A null
    /// task means the run was never started — the page failed before the model was reached — which is
    /// safe to free.
    /// </summary>
    internal static bool RunIsStillGoing(Task? abandoned) => abandoned is { IsCompleted: false };

    /// <summary>
    /// Observe the fault of a detection task that <c>Task.WaitAsync</c> may abandon on a timeout or
    /// a cancellation, so a later native failure does not surface as an unobserved task exception on
    /// the finalizer thread. A successful or cancelled task never triggers the continuation.
    /// </summary>
    private static void ObserveIfAbandoned(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Image dimensions from the file header, used only to scale the detection timeout by the page
    /// size the way the upscale paths do. Throws like any decode failure, so the caller's existing
    /// error path handles an unreadable page.
    /// </summary>
    private static (int Width, int Height) ReadImageSize(string imagePath)
    {
        using var image = NetVips.Image.NewFromFile(imagePath, access: NetVips.Enums.Access.Random);
        return (image.Width, image.Height);
    }

    /// <summary>
    /// Per-page bound on the awaited detection, scaled by the page's pixel count like the upscale
    /// paths (<c>UpscaleTimeout × max(1, megapixels)</c>). A non-positive configured timeout still
    /// gets <see cref="DefaultDetectionTimeout"/>: detection has no other watchdog, so honouring
    /// "disabled" here would re-introduce the indefinite park this bound exists to prevent.
    /// </summary>
    private TimeSpan ResolveDetectionTimeout(int width, int height)
    {
        TimeSpan configured = upscalerConfig?.Value.UpscaleTimeout ?? TimeSpan.Zero;
        TimeSpan basis = configured > TimeSpan.Zero ? configured : DefaultDetectionTimeout;
        double scaling = Math.Max(1.0, (double)width * height / 1_000_000.0);
        return basis * scaling;
    }

    /// <summary>
    /// Maps a peak row of the resized page back to a coordinate in the original, clamped to the last
    /// real row. A row at the very bottom of the resized page can round up to
    /// <paramref name="origHeight"/> on a short page; downstream <c>SplitApplier</c> drops a split at
    /// or below the image height (<c>splitY &gt;= image.Height</c>), so without the clamp the
    /// bottom-edge split would be silently lost.
    /// </summary>
    internal static int MapRowToOriginal(int resizedRow, double vscale, int origHeight)
    {
        int lastRow = Math.Max(0, origHeight - 1);
        if (vscale <= 0)
        {
            return Math.Clamp(resizedRow, 0, lastRow);
        }

        int mapped = (int)Math.Round(resizedRow / vscale);
        return Math.Clamp(mapped, 0, lastRow);
    }

    /// <summary>
    /// Verifies once per session that the loaded graph really is the detector the row loop assumes: an
    /// input named <see cref="ModelInputName"/> carrying float32, plus
    /// <see cref="ProbabilitiesOutputName"/> (float32) and <see cref="PeakMaskOutputName"/> (bool)
    /// outputs.
    /// <para>
    /// The pinned export declares exactly those, but nothing enforces that the file at the model path
    /// is that export: <see cref="SplitDetectionLayout.ResolveModelPath"/> accepts any file that
    /// exists, and the pinned hash only repairs a swapped file while models are being verified at
    /// startup — a running process keeps using whatever it loaded. Without this check an unrecognised
    /// name comes back as ONNX Runtime's "is not in the metadata" and an unrecognised element type as a
    /// bare <see cref="NullReferenceException"/> out of <c>AsTensor</c>, both as a per-page structural
    /// failure whose message names neither the model nor the tensor, repeated for every page of every
    /// chapter.
    /// </para>
    /// </summary>
    private static void RequireDetectorContract(InferenceSession session)
    {
        if (!session.InputMetadata.TryGetValue(ModelInputName, out NodeMetadata? input))
        {
            throw new ModelContractMismatchException(
                $"The detector model has no input named '{ModelInputName}'; its inputs are "
                    + $"{OnnxTiler.FormatNames(session.InputMetadata.Keys)}. The file is not the "
                    + "page-break detector this build expects — delete it and let the app download it "
                    + "again."
            );
        }

        if (input.ElementType != typeof(float))
        {
            throw new ModelContractMismatchException(
                $"The detector model's '{ModelInputName}' input is {input.ElementType.Name}; float32 is "
                    + "required."
            );
        }

        // The height is dynamic (the resized page or a window), but the width is always
        // DetectorInputWidth. A model that bakes another width would reject every Run with a shape
        // error that names neither the model nor the expectation, and — coming out of the session —
        // would be remembered as a structural failure for the process. A fixed positive width other
        // than the expected one is that same wrong file, reported clearly and once.
        if (
            input.Dimensions.Length == 4
            && input.Dimensions[3] > 0
            && input.Dimensions[3] != DetectorInputWidth
        )
        {
            throw new ModelContractMismatchException(
                $"The detector model's '{ModelInputName}' input has a fixed width of "
                    + $"{input.Dimensions[3]}, but this build resizes every page to {DetectorInputWidth} "
                    + "px. The file is not the page-break detector this build expects — delete it and let "
                    + "the app download it again."
            );
        }

        RequireDetectorOutput(session, ProbabilitiesOutputName, typeof(float));
        RequireDetectorOutput(session, PeakMaskOutputName, typeof(bool));
    }

    private static void RequireDetectorOutput(
        InferenceSession session,
        string name,
        Type expectedElementType
    )
    {
        if (!session.OutputMetadata.TryGetValue(name, out NodeMetadata? output))
        {
            throw new ModelContractMismatchException(
                $"The detector model has no output named '{name}'; its outputs are "
                    + $"{OnnxTiler.FormatNames(session.OutputMetadata.Keys)}. The file is not the "
                    + "page-break detector this build expects — delete it and let the app download it "
                    + "again."
            );
        }

        if (output.ElementType != expectedElementType)
        {
            throw new ModelContractMismatchException(
                $"The detector model's '{name}' output is {output.ElementType.Name}; "
                    + $"{expectedElementType.Name} is required."
            );
        }
    }

    /// <summary>
    /// Rejects a detector output whose geometry is not "one score per resized row" in both the
    /// <c>peak_mask</c> and <c>probabilities</c> outputs, so the row loop cannot silently truncate a
    /// longer output or read past a shorter one.
    /// </summary>
    private static void EnsureDetectorOutputGeometry(
        Tensor<bool> peakMaskTensor,
        Tensor<float> probsTensor,
        int expectedRows
    )
    {
        if (
            peakMaskTensor.Dimensions.Length == 2
            && probsTensor.Dimensions.Length == 2
            && peakMaskTensor.Dimensions[0] == 1
            && probsTensor.Dimensions[0] == 1
            && peakMaskTensor.Dimensions[1] == expectedRows
            && probsTensor.Dimensions[1] == expectedRows
        )
        {
            return;
        }

        // ModelContractMismatchException, like RequireDetectorContract and the upscaler's
        // equivalent checks: this is a property of the loaded model (its baked output geometry),
        // which the known-failure cache may remember and replacing the model file clears.
        throw new ModelContractMismatchException(
            $"The detector returned an unexpected output shape: peak_mask is "
                + $"[{string.Join(", ", peakMaskTensor.Dimensions.ToArray())}] and probabilities is "
                + $"[{string.Join(", ", probsTensor.Dimensions.ToArray())}], but one row per row of "
                + $"the {expectedRows}-row resized page was expected."
        );
    }
}
