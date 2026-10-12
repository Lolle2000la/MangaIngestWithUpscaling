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
    /// Rows of a page inferred per forward pass. Matches the reference's window (and the model's
    /// training crop), which is what keeps the window's input length in the range the model was fitted
    /// on; the value only affects throughput, not the result, because the margin below is what makes a
    /// window exact.
    /// </summary>
    private const int DetectorChunkRows = 2048;

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
    /// Compares keys case-insensitively on the path, matching how the session factory resolves model
    /// paths.
    /// </summary>
    private sealed class DetectorBackendKeyComparer : IEqualityComparer<DetectorBackendKey>
    {
        public static readonly DetectorBackendKeyComparer Instance = new();

        public bool Equals(DetectorBackendKey x, DetectorBackendKey y) =>
            x.Backend == y.Backend
            && string.Equals(x.ModelPath, y.ModelPath, StringComparison.OrdinalIgnoreCase)
            && x.Signature == y.Signature;

        public int GetHashCode(DetectorBackendKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModelPath),
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
    private static bool IsRetryableDetectionFailure(Exception ex) =>
        OnnxTiler.IsMemoryException(ex)
        || ex is IOException
        // Aligned with the upscaler's classifiers, which walk the chain for UnauthorizedAccessException:
        // a mount that is briefly unavailable during detection should not permanently fail the chapter
        // when the same condition during upscaling is treated as retryable.
        || ex is UnauthorizedAccessException
        || ex is NonFiniteModelOutputException;

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

                    const int targetWidth = 768;
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

                    // The inference runs in windows rather than over the whole page at once. The model's
                    // vertical receptive field is +-1423 rows — the dilated CNN stack reaches +-1023
                    // (dilations 1..512 over 3x3 kernels), the smoothing conv +-8 and the peak test's
                    // MaxPool +-400 — so a window that extends DetectorWindowMargin rows past the rows
                    // it is trusted for reproduces the whole-page result exactly. Measured against the
                    // shipped model on periodic, noise and banded 9000-row pages: max |delta prob| is
                    // exactly 0.0 at this margin, 3.8e-6 at 1024 and 1.5e-1 at 512.
                    //
                    // Bounding the window is what removes the memory growth: a whole-page pass needs
                    // about 1.5 GiB at 4000 resized rows and ~5.5 GiB at 15000, which is the top of the
                    // range the model's training strips covered. With this it is the footprint of one
                    // window regardless of page height.
                    //
                    // The margin is deliberately larger than the reference's 512-row overlap. Upstream
                    // windows the *logits* before the calibration is applied, so its overlap only has
                    // to cover the peak test's +-400; its 2048/512 window therefore truncates the
                    // dilated CNN's +-1023 context for rows near a window boundary. That is consistent
                    // with the 2048-row crops the model was trained on, but it is *not* the whole-page
                    // evaluation the baked thresholds were calibrated on (calibrate.py runs an
                    // uncropped single pass). Using the exact margin keeps this on the calibrated
                    // evaluation, and makes the result independent of the window size.
                    var probs = new float[actualHeight];
                    var peakMask = new bool[actualHeight];

                    for (
                        int windowStart = 0;
                        windowStart < actualHeight;
                        windowStart += DetectorChunkRows
                    )
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int windowEnd = Math.Min(windowStart + DetectorChunkRows, actualHeight);
                        int inputStart = Math.Max(0, windowStart - DetectorWindowMargin);
                        int inputEnd = Math.Min(actualHeight, windowEnd + DetectorWindowMargin);
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
                                sizeof(float) * ((plane * planeSize) + (inputStart * actualWidth)),
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
                        EnsureDetectorOutputGeometry(windowPeakMask, windowProbs, windowHeight);

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
        catch (Exception ex) when (reachedModel)
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

        throw new InvalidOperationException(
            $"The detector returned an unexpected output shape: peak_mask is "
                + $"[{string.Join(", ", peakMaskTensor.Dimensions.ToArray())}] and probabilities is "
                + $"[{string.Join(", ", probsTensor.Dimensions.ToArray())}], but one row per row of "
                + $"the {expectedRows}-row resized page was expected."
        );
    }
}
