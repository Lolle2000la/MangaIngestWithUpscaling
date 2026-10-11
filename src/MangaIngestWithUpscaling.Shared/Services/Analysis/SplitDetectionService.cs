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

        logger.LogInformation("Running in-process ONNX split detection on {ImagePath}", imagePath);

        // Only a failure that came out of the session itself should drop the cached session. The
        // image is decoded before any session is requested, so whether this was ever assigned is
        // what separates an inference failure from a decode failure of an unrelated, healthy model.
        InferenceSession? session = null;

        try
        {
            return await Task.Run(
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

                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                        localSession.Run(inputs);

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
                            int yOrig = (int)Math.Round(y / vscale);
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running ONNX split detection for {ImagePath}", imagePath);

            // Only one session is cached at a time (OnnxSessionFactory.MaxCachedSessions), so a
            // session that keeps failing would be reused, and fail again, for every remaining page
            // of the chapter. Drop it so the next page rebuilds a fresh one. A decode-time failure
            // never fetched a session, so it must leave a healthy cached one in place.
            if (session is not null)
            {
                onnxSessionFactory.InvalidateSession(modelPath);
            }

            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
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
