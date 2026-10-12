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
    public const int CURRENT_DETECTOR_VERSION = 1;

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

    /// <summary>Model path and resolved accelerator backend a failure verdict applies to.</summary>
    private readonly record struct DetectorBackendKey(string ModelPath, GpuBackend Backend);

    /// <summary>
    /// Compares keys case-insensitively on the path, matching how the session factory resolves model
    /// paths.
    /// </summary>
    private sealed class DetectorBackendKeyComparer : IEqualityComparer<DetectorBackendKey>
    {
        public static readonly DetectorBackendKeyComparer Instance = new();

        public bool Equals(DetectorBackendKey x, DetectorBackendKey y) =>
            x.Backend == y.Backend
            && string.Equals(x.ModelPath, y.ModelPath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DetectorBackendKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModelPath),
                obj.Backend
            );
    }

    private static bool TryGetKnownFailure(string modelPath, GpuBackend backend, out string error)
    {
        lock (KnownFailuresGate)
        {
            return KnownFailures.TryGetValue(
                new DetectorBackendKey(modelPath, backend),
                out error!
            );
        }
    }

    private static void RecordKnownFailure(string modelPath, GpuBackend backend, string error)
    {
        lock (KnownFailuresGate)
        {
            var key = new DetectorBackendKey(modelPath, backend);
            if (KnownFailures.Count >= KnownFailureCacheCapacity && !KnownFailures.ContainsKey(key))
            {
                KnownFailures.Clear();
            }

            KnownFailures[key] = error;
        }
    }

    /// <summary>
    /// True for a detection failure that says nothing about the detector: a resource error (memory,
    /// a lost device) or an IO error. Such a failure must be retried, not cached as unusable.
    /// Mirrors <c>OnnxUpscaleEngine.IsTransientProbeFailure</c>.
    /// </summary>
    private static bool IsTransientDetectionFailure(Exception ex) =>
        OnnxTiler.IsMemoryException(ex) || ex is IOException;

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
                    using var flattened = resized.HasAlpha() ? resized.Flatten() : resized.Copy();
                    using var rgb =
                        flattened.Bands == 3
                        && flattened.Interpretation == NetVips.Enums.Interpretation.Srgb
                            ? flattened.Copy()
                            : flattened.Colourspace(NetVips.Enums.Interpretation.Srgb);
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

                    var inputTensor = new DenseTensor<float>(
                        tensorData,
                        [1, 3, actualHeight, actualWidth]
                    );
                    var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", inputTensor) };

                    // The two outputs are named explicitly rather than passing null for "all": with a
                    // RunOptions the null-outputNames overload does not hand back every output, and
                    // these are the only two this reads.
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                        localSession.Run(
                            inputs,
                            new[] { "peak_mask", "probabilities" },
                            runOptions
                        );

                    var peakMaskTensor = outputs.First(o => o.Name == "peak_mask").AsTensor<bool>();
                    var probsTensor =
                        (DenseTensor<float>)
                            outputs.First(o => o.Name == "probabilities").AsTensor<float>();

                    // A mis-executing execution provider can return an all-NaN output instead of
                    // failing (the same failure class OnnxTiler rejects for the upscaler). Every
                    // comparison against NaN is false, so without this the mask is all-false and the
                    // page is reported as a successful detection with zero splits — a silent miss.
                    if (OnnxTiler.ContainsNonFinite(probsTensor.Buffer.Span))
                    {
                        throw new NonFiniteModelOutputException();
                    }

                    // Both outputs must be one score per resized row. A longer output would be
                    // silently truncated by the loop below and a shorter one read past its end, so
                    // an unexpected geometry is rejected rather than quietly mis-detected.
                    EnsureDetectorOutputGeometry(peakMaskTensor, probsTensor, actualHeight);

                    const int edgeMargin = 100;
                    bool applyEdgeMargin = edgeMargin > 0 && actualHeight > edgeMargin * 2;
                    var splits = new List<DetectedSplit>();

                    for (int y = 0; y < actualHeight; y++)
                    {
                        if (applyEdgeMargin && (y < edgeMargin || y >= actualHeight - edgeMargin))
                        {
                            continue;
                        }

                        if (peakMaskTensor[0, y])
                        {
                            int yOrig = MapRowToOriginal(y, vscale, origHeight);
                            float confidence = probsTensor[0, y];
                            splits.Add(
                                new DetectedSplit
                                {
                                    YOriginal = yOrig,
                                    Confidence = Math.Round(confidence, 4),
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
        catch (Exception ex) when (IsTransientDetectionFailure(ex))
        {
            // Transient: a resource hiccup (memory, a lost device) or an IO error, not a property of
            // the detector. Deliberately not gated on having reached the model: the vips decode and
            // the float[1*3*H*W] tensor it feeds happen before the session is fetched, and an OOM
            // there is just as transient as one inside Run. Gated, it fell through to the generic
            // catch and was reported as a per-page error, which ends in SetFailedAsync. Drop the
            // session for the retry when there is one.
            logger.LogWarning(
                ex,
                "Split detection for {ImagePath} failed transiently; the task will be retried.",
                imagePath
            );
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            // Rethrown with its original type, not reported as a per-page error. A recorded error
            // reaches SplitProcessingService as a failed finding, and when every page carries one it
            // calls SetFailedAsync and throws — the chapter is marked failed for good, on the strength
            // of an OOM or a lost device. Rethrown, the worker's IsInfrastructureFailure /
            // ClassifyStreamingFailure path recognises it as transient and requeues the task with the
            // spool intact, which is exactly what the same machinery does on the upscaler path.
            throw;
        }
        catch (Exception ex) when (reachedModel && ex is not ObjectDisposedException)
        {
            logger.LogError(ex, "Error running ONNX split detection for {ImagePath}", imagePath);

            // Only one session is cached at a time (OnnxSessionFactory.MaxCachedSessions), so a
            // session that keeps failing would be reused, and fail again, for every remaining page
            // of the chapter. Drop it so the next page rebuilds a fresh one, and remember the
            // structural failure for this backend so the rebuild does not happen on every page of
            // every chapter either.
            // A disposed factory is excluded above: it means shutdown, not a property of the detector,
            // and caching it would disable detection for the rest of the process on the strength of a
            // teardown race.
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            // A non-finite output is not a structural verdict. The model loaded and ran; the comment
            // at the throw calls an all-NaN output the mark of a mis-executing provider, which is a
            // glitch as much as a defect — and with a single detector, caching it disables detection
            // for the rest of the process on the strength of one page. The session is dropped above,
            // so the next page rebuilds and re-probes.
            if (ex is not NonFiniteModelOutputException)
            {
                RecordKnownFailure(modelPath, backend, ex.Message);
            }

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
