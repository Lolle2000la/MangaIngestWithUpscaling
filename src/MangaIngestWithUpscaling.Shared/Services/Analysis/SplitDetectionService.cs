using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

[RegisterScoped]
public class SplitDetectionService(
    IPythonService pythonService,
    IMangaJaNaiWorkerClient workerClient,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<SplitDetectionService> logger,
    IStringLocalizer<SplitDetectionService> localizer
) : ISplitDetectionService
{
    public const int CURRENT_DETECTOR_VERSION = 1;

    private const string SubmodulePath = "backend/src/manga-vert-split-nn";
    private const string ScriptName = "detect_breaks.py";
    private const string ModelPath = "models/BCE Only (v8)/final_deployment/best_model.pth";
    private const string ConfigPath = "models/BCE Only (v8)/final_deployment/model_config.json";

    public async Task<List<SplitDetectionResult>> DetectSplitsAsync(
        string inputPath,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // The persistent upscaling worker keeps its models and the PyTorch caching
        // allocator resident on the GPU, which can starve the per-image detection
        // process of VRAM on smaller GPUs. By default we ask it to release its cached
        // VRAM (it stays warm and is reused by the next upscale job); when configured,
        // it is shut down entirely for maximum free VRAM.
        await ReleaseUpscalerGpuResourcesAsync();

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
                    ImageConstants.SupportedImageExtensions.Contains(Path.GetExtension(f).ToLower())
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

    private async Task ReleaseUpscalerGpuResourcesAsync()
    {
        try
        {
            if (upscalerConfig.Value.ShutdownWorkerBeforeSplitDetection)
            {
                // VRAM-limited setup: even an idle worker (model weights + CUDA context)
                // can starve detection, so tear the worker down completely. It is
                // respawned lazily on the next upscale job.
                await workerClient.ShutdownWorkerAsync(CancellationToken.None);
            }
            else
            {
                await workerClient.ReleaseGpuCacheAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: failing to free VRAM must not prevent detection from
            // being attempted, it may just hit CUDA OOM on small GPUs otherwise.
            logger.LogWarning(
                ex,
                "Failed to free the upscaling worker's VRAM before split detection."
            );
        }
    }

    private async Task<SplitDetectionResult> DetectSingleImageAsync(
        string imagePath,
        CancellationToken cancellationToken
    )
    {
        var baseDir = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(baseDir, SubmodulePath, ScriptName);
        var checkpointPath = Path.Combine(baseDir, SubmodulePath, ModelPath);
        var configPath = Path.Combine(baseDir, SubmodulePath, ConfigPath);

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(localizer["Error_ScriptNotFound", scriptPath]);
        }
        if (!File.Exists(checkpointPath))
        {
            throw new FileNotFoundException(localizer["Error_CheckpointNotFound", checkpointPath]);
        }
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(localizer["Error_ConfigNotFound", configPath]);
        }

        // Quote paths to handle spaces
        var args =
            $"--checkpoint \"{checkpointPath}\" --config \"{configPath}\" --image \"{imagePath}\"";

        logger.LogInformation("Running split detection on {ImagePath}", imagePath);

        try
        {
            // Set a reasonable timeout, e.g., 2 minutes per image
            var timeout = TimeSpan.FromMinutes(2);
            var output = await pythonService.RunPythonScript(
                scriptPath,
                args,
                cancellationToken,
                timeout
            );

            try
            {
                var result = JsonSerializer.Deserialize(
                    output,
                    SharedJsonContext.Default.SplitDetectionResult
                );
                if (result == null)
                {
                    throw new JsonException(localizer["Error_DeserializationFailed"]);
                }
                return result;
            }
            catch (JsonException)
            {
                var jsonStartIndex = output.IndexOf('{');
                var jsonEndIndex = output.LastIndexOf('}');
                if (jsonStartIndex >= 0 && jsonEndIndex > jsonStartIndex)
                {
                    var json = output.Substring(jsonStartIndex, jsonEndIndex - jsonStartIndex + 1);
                    var result = JsonSerializer.Deserialize(
                        json,
                        SharedJsonContext.Default.SplitDetectionResult
                    );
                    if (result != null)
                    {
                        return result;
                    }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running split detection for {ImagePath}", imagePath);
            return new SplitDetectionResult { ImagePath = imagePath, Error = ex.Message };
        }
    }
}
