using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

[RegisterSingleton]
public sealed class OnnxSessionFactory(
    IOptions<UpscalerConfig> config,
    ILogger<OnnxSessionFactory> logger
) : IOnnxSessionFactory
{
    private readonly ConcurrentDictionary<string, InferenceSession> _sessions = new();
    private bool _disposed;

    public InferenceSession GetOrCreateSession(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string fullPath = Path.GetFullPath(modelPath);
        return _sessions.GetOrAdd(fullPath, CreateSession);
    }

    public InferenceSession CreateSession(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ONNX model not found: {modelPath}", modelPath);
        }

        UpscalerConfig currentConfig = config.Value;
        int deviceId = Math.Max(0, currentConfig.SelectedDeviceIndex - 1);
        bool forceCpu = currentConfig.UseCPU || currentConfig.SelectedDeviceIndex <= 0;

        SessionOptions options = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        if (forceCpu)
        {
            logger.LogInformation(
                "Creating ONNX session for {Model} using CPU execution provider.",
                Path.GetFileName(modelPath)
            );
            return new InferenceSession(modelPath, options);
        }

        // Try GPU providers based on platform and backend configuration
        bool gpuConfigured = TryConfigureGpuProvider(
            options,
            currentConfig,
            deviceId,
            Path.GetFileName(modelPath)
        );

        try
        {
            return new InferenceSession(modelPath, options);
        }
        catch (Exception ex) when (gpuConfigured)
        {
            logger.LogWarning(
                ex,
                "Failed to initialize ONNX session with hardware acceleration for {Model}. Falling back to CPU.",
                Path.GetFileName(modelPath)
            );

            // Fallback cleanly to CPU
            SessionOptions cpuOptions = new()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            return new InferenceSession(modelPath, cpuOptions);
        }
    }

    private bool TryConfigureGpuProvider(
        SessionOptions options,
        UpscalerConfig currentConfig,
        int deviceId,
        string modelName
    )
    {
        GpuBackend backend = currentConfig.PreferredGpuBackend;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (backend is GpuBackend.CUDA or GpuBackend.CUDA_12_8)
            {
                try
                {
                    logger.LogInformation(
                        "Configuring CUDA execution provider (device {DeviceId}) for {Model}",
                        deviceId,
                        modelName
                    );
                    options.AppendExecutionProvider_CUDA(deviceId);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to append CUDA execution provider. Will try CPU fallback."
                    );
                    return false;
                }
            }

            // For AMD / Auto / ROCm / ROCm_GFX120X on Linux: try MIGraphX
            try
            {
                logger.LogInformation(
                    "Configuring MIGraphX execution provider (device {DeviceId}) for {Model}",
                    deviceId,
                    modelName
                );
                options.AppendExecutionProvider_MIGraphX(deviceId);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to append MIGraphX execution provider. Will try CPU fallback."
                );
                return false;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (backend is GpuBackend.CUDA or GpuBackend.CUDA_12_8)
            {
                try
                {
                    logger.LogInformation(
                        "Configuring CUDA execution provider (device {DeviceId}) for {Model}",
                        deviceId,
                        modelName
                    );
                    options.AppendExecutionProvider_CUDA(deviceId);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to append CUDA execution provider. Will try DirectML/CPU."
                    );
                }
            }

            try
            {
                logger.LogInformation(
                    "Configuring DirectML execution provider (device {DeviceId}) for {Model}",
                    deviceId,
                    modelName
                );
                options.AppendExecutionProvider_DML(deviceId);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to append DirectML execution provider. Will try CPU fallback."
                );
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
