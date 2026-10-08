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
        CancellationToken cancellationToken = default,
        bool releaseUpscalerGpu = true
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

                    InferenceSession session = onnxSessionFactory.GetOrCreateSession(modelPath);
                    using var inferenceScope = onnxSessionFactory.EnterInferenceScope();

                    var inputTensor = new DenseTensor<float>(
                        tensorData,
                        [1, 3, actualHeight, actualWidth]
                    );
                    var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", inputTensor) };

                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                        session.Run(inputs);

                    var peakMaskTensor = outputs.First(o => o.Name == "peak_mask").AsTensor<bool>();
                    var probsTensor = outputs
                        .First(o => o.Name == "probabilities")
                        .AsTensor<float>();

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
            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
        }
    }
}
