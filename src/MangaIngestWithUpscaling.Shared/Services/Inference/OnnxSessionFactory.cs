using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

[RegisterSingleton]
public sealed class OnnxSessionFactory(
    IOptions<UpscalerConfig> config,
    ILogger<OnnxSessionFactory> logger
) : IOnnxSessionFactory
{
    private static readonly Lock WebGpuInitLock = new();
    private static bool _webGpuRegistered;
    private static bool? _cudaAvailable;
    private static bool? _openVinoAvailable;
    private static bool? _migraphxAvailable;
    private static bool? _directMlAvailable;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly LinkedList<string> _lruOrder = new();
    private readonly ReaderWriterLockSlim _activeExecutionLock = new(
        LockRecursionPolicy.SupportsRecursion
    );
    public const int MaxCachedSessions = 4;
    private bool _disposed;

    public IDisposable EnterInferenceScope()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeExecutionLock.EnterReadLock();
        return new InferenceScopeDisposable(_activeExecutionLock);
    }

    private sealed class InferenceScopeDisposable(ReaderWriterLockSlim lockSlim) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lockSlim.ExitReadLock();
            }
        }
    }

    public InferenceSession GetOrCreateSession(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string fullPath = Path.GetFullPath(modelPath);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sessions.TryGetValue(fullPath, out var existing))
            {
                _lruOrder.Remove(fullPath);
                _lruOrder.AddLast(fullPath);
                return existing;
            }

            // Evict least-recently-used sessions if at capacity
            while (_sessions.Count >= MaxCachedSessions && _lruOrder.Count > 0)
            {
                string oldestPath = _lruOrder.First!.Value;
                _lruOrder.RemoveFirst();

                if (_sessions.Remove(oldestPath, out var oldSession))
                {
                    logger.LogInformation(
                        "Evicting least recently used model {OldModel} to free GPU memory (capacity {Capacity})",
                        Path.GetFileName(oldestPath),
                        MaxCachedSessions
                    );

                    _activeExecutionLock.EnterWriteLock();
                    try
                    {
                        oldSession.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to cleanly dispose evicted session for {Model}",
                            Path.GetFileName(oldestPath)
                        );
                    }
                    finally
                    {
                        _activeExecutionLock.ExitWriteLock();
                    }
                }
            }

            InferenceSession session = CreateSession(fullPath);
            _sessions[fullPath] = session;
            _lruOrder.AddLast(fullPath);
            return session;
        }
    }

    public void InvalidateSession(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string fullPath = Path.GetFullPath(modelPath);
        InferenceSession? sessionToDispose = null;

        lock (_lock)
        {
            if (_disposed)
                return;
            if (_sessions.Remove(fullPath, out var session))
            {
                _lruOrder.Remove(fullPath);
                sessionToDispose = session;
            }
        }

        if (sessionToDispose != null)
        {
            _activeExecutionLock.EnterWriteLock();
            try
            {
                sessionToDispose.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to cleanly dispose invalidated session for {Model}",
                    Path.GetFileName(modelPath)
                );
            }
            finally
            {
                _activeExecutionLock.ExitWriteLock();
            }
        }
    }

    public void InvalidateAllSessions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<InferenceSession> sessionsToDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            sessionsToDispose = [.. _sessions.Values];
            _sessions.Clear();
            _lruOrder.Clear();
        }

        if (sessionsToDispose.Count > 0)
        {
            _activeExecutionLock.EnterWriteLock();
            try
            {
                foreach (var session in sessionsToDispose)
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to cleanly dispose session during InvalidateAllSessions"
                        );
                    }
                }
            }
            finally
            {
                _activeExecutionLock.ExitWriteLock();
            }
        }
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
            EnableMemoryPattern = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        options.AddSessionConfigEntry("session.arena_extend_strategy", "kSameAsRequested");
        options.AddSessionConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0;gpu:0");

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
                EnableMemoryPattern = false,
            };
            cpuOptions.AddSessionConfigEntry("session.arena_extend_strategy", "kSameAsRequested");
            cpuOptions.AddSessionConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0;gpu:0");
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

#pragma warning disable CS0618
        if (backend is GpuBackend.ROCm or GpuBackend.ROCm_GFX120X or GpuBackend.XPU)
        {
            backend = GpuBackend.WebGPU;
        }
        else if (backend is GpuBackend.CUDA_12_8)
        {
            backend = GpuBackend.CUDA;
        }
#pragma warning restore CS0618

        if (backend == GpuBackend.WebGPU)
        {
            return TryConfigureWebGpu(options, deviceId, modelName);
        }

        if (backend == GpuBackend.CUDA)
        {
            return TryConfigureCuda(options, deviceId, modelName);
        }

        if (backend == GpuBackend.DirectML)
        {
            return TryConfigureDirectML(options, deviceId, modelName);
        }

        if (backend == GpuBackend.OpenVINO)
        {
            return TryConfigureOpenVino(options, deviceId, modelName);
        }

        if (backend == GpuBackend.MIGraphX)
        {
            return TryConfigureMIGraphX(options, deviceId, modelName);
        }

        // GpuBackend.Auto: Platform-specific discovery
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Auto on Linux: Try CUDA -> WebGPU -> CPU
            if (TryConfigureCuda(options, deviceId, modelName))
            {
                return true;
            }

            if (TryConfigureWebGpu(options, deviceId, modelName))
            {
                return true;
            }

            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Auto on Windows: Try WebGPU -> DirectML -> CUDA -> CPU
            if (TryConfigureWebGpu(options, deviceId, modelName))
            {
                return true;
            }

            if (TryConfigureDirectML(options, deviceId, modelName))
            {
                return true;
            }

            return TryConfigureCuda(options, deviceId, modelName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TryConfigureWebGpu(options, deviceId, modelName);
        }

        return false;
    }

    private bool TryConfigureCuda(SessionOptions options, int deviceId, string modelName)
    {
        if (_cudaAvailable == false)
        {
            return false;
        }

        try
        {
            logger.LogInformation(
                "Configuring CUDA execution provider (device {DeviceId}) for {Model}",
                deviceId,
                modelName
            );
            options.AppendExecutionProvider_CUDA(deviceId);
            _cudaAvailable = true;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            logger.LogDebug(
                "CUDA execution provider entry point not found in this ONNX Runtime build."
            );
            _cudaAvailable = false;
            return false;
        }
        catch (DllNotFoundException)
        {
            logger.LogDebug("CUDA runtime libraries not found on this system.");
            _cudaAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append CUDA execution provider. Will try next provider or CPU fallback."
            );
            _cudaAvailable = false;
            return false;
        }
    }

    private bool TryConfigureOpenVino(SessionOptions options, int deviceId, string modelName)
    {
        if (_openVinoAvailable == false)
        {
            return false;
        }

        try
        {
            string targetDevice = deviceId > 0 ? $"GPU.{deviceId}" : "GPU";
            logger.LogInformation(
                "Configuring OpenVINO execution provider ({Device}) for {Model}",
                targetDevice,
                modelName
            );
            options.AppendExecutionProvider_OpenVINO(targetDevice);
            _openVinoAvailable = true;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            logger.LogDebug(
                "OpenVINO execution provider entry point not found in this ONNX Runtime build."
            );
            _openVinoAvailable = false;
            return false;
        }
        catch (DllNotFoundException)
        {
            logger.LogDebug("OpenVINO runtime libraries not found on this system.");
            _openVinoAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append OpenVINO execution provider. Will try next provider or CPU fallback."
            );
            _openVinoAvailable = false;
            return false;
        }
    }

    private bool TryConfigureMIGraphX(SessionOptions options, int deviceId, string modelName)
    {
        if (_migraphxAvailable == false)
        {
            return false;
        }

        try
        {
            logger.LogInformation(
                "Configuring MIGraphX execution provider (device {DeviceId}) for {Model}",
                deviceId,
                modelName
            );
            options.AppendExecutionProvider_MIGraphX(deviceId);
            _migraphxAvailable = true;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            logger.LogDebug(
                "MIGraphX execution provider entry point not found in this ONNX Runtime build."
            );
            _migraphxAvailable = false;
            return false;
        }
        catch (DllNotFoundException)
        {
            logger.LogDebug("MIGraphX runtime libraries not found on this system.");
            _migraphxAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append MIGraphX execution provider. Will try next provider or CPU fallback."
            );
            _migraphxAvailable = false;
            return false;
        }
    }

    private bool TryConfigureDirectML(SessionOptions options, int deviceId, string modelName)
    {
        if (_directMlAvailable == false)
        {
            return false;
        }

        try
        {
            logger.LogInformation(
                "Configuring DirectML execution provider (device {DeviceId}) for {Model}",
                deviceId,
                modelName
            );
            options.AppendExecutionProvider_DML(deviceId);
            _directMlAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append DirectML execution provider. Will try CPU fallback."
            );
            _directMlAvailable = false;
            return false;
        }
    }

    private bool TryConfigureWebGpu(SessionOptions options, int deviceId, string modelName)
    {
        try
        {
            var env = OrtEnv.Instance();
            if (!_webGpuRegistered)
            {
                lock (WebGpuInitLock)
                {
                    if (!_webGpuRegistered)
                    {
                        string libPath = WebGpuEp.GetLibraryPath();
                        if (File.Exists(libPath))
                        {
                            env.RegisterExecutionProviderLibrary("webgpu_ep", libPath);
                            _webGpuRegistered = true;
                        }
                        else
                        {
                            logger.LogWarning("WebGPU library not found at: {LibPath}", libPath);
                            return false;
                        }
                    }
                }
            }

            OrtEpDevice? webGpuDevice = null;
            int foundIndex = 0;
            foreach (var d in env.GetEpDevices())
            {
                if (d.EpName == WebGpuEp.GetEpName())
                {
                    if (foundIndex == deviceId)
                    {
                        webGpuDevice = d;
                        break;
                    }
                    foundIndex++;
                }
            }

            if (webGpuDevice is null && foundIndex > 0)
            {
                foreach (var d in env.GetEpDevices())
                {
                    if (d.EpName == WebGpuEp.GetEpName())
                    {
                        webGpuDevice = d;
                        break;
                    }
                }
            }

            if (webGpuDevice is null)
            {
                logger.LogWarning("No WebGPU compatible device found for {Model}.", modelName);
                return false;
            }

            logger.LogInformation(
                "Configuring WebGPU execution provider ({Device}) for {Model}",
                webGpuDevice.EpName,
                modelName
            );

            var webGpuOptions = new Dictionary<string, string>
            {
                { "storageBufferCacheMode", "simple" },
                { "defaultBufferCacheMode", "simple" },
                { "uniformBufferCacheMode", "simple" },
            };

            options.AppendExecutionProvider(env, new[] { webGpuDevice }, webGpuOptions);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append WebGPU execution provider. Will try next provider or CPU fallback."
            );
            return false;
        }
    }

    public GpuBackend GetEffectiveBackend()
    {
        UpscalerConfig currentConfig = config.Value;
        if (currentConfig.UseCPU || currentConfig.SelectedDeviceIndex <= 0)
        {
            return GpuBackend.CPU;
        }

        GpuBackend backend = currentConfig.PreferredGpuBackend;
#pragma warning disable CS0618
        if (backend is GpuBackend.ROCm or GpuBackend.ROCm_GFX120X or GpuBackend.XPU)
        {
            backend = GpuBackend.WebGPU;
        }
        else if (backend is GpuBackend.CUDA_12_8)
        {
            backend = GpuBackend.CUDA;
        }
#pragma warning restore CS0618

        if (backend != GpuBackend.Auto)
        {
            return backend;
        }

        int deviceId = Math.Max(0, currentConfig.SelectedDeviceIndex - 1);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            using var testOptions = new SessionOptions();
            if (TryConfigureCuda(testOptions, deviceId, "probe"))
            {
                return GpuBackend.CUDA;
            }
            if (TryConfigureWebGpu(testOptions, deviceId, "probe"))
            {
                return GpuBackend.WebGPU;
            }
            return GpuBackend.CPU;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var testOptions = new SessionOptions();
            if (TryConfigureWebGpu(testOptions, deviceId, "probe"))
            {
                return GpuBackend.WebGPU;
            }
            if (TryConfigureDirectML(testOptions, deviceId, "probe"))
            {
                return GpuBackend.DirectML;
            }
            if (TryConfigureCuda(testOptions, deviceId, "probe"))
            {
                return GpuBackend.CUDA;
            }
            return GpuBackend.CPU;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            using var testOptions = new SessionOptions();
            if (TryConfigureWebGpu(testOptions, deviceId, "probe"))
            {
                return GpuBackend.WebGPU;
            }
            return GpuBackend.CPU;
        }

        return GpuBackend.CPU;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<InferenceSession> sessionsToDispose;
        lock (_lock)
        {
            sessionsToDispose = [.. _sessions.Values];
            _sessions.Clear();
            _lruOrder.Clear();
        }

        _activeExecutionLock.EnterWriteLock();
        try
        {
            foreach (var session in sessionsToDispose)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to cleanly dispose session during factory disposal"
                    );
                }
            }
        }
        finally
        {
            _activeExecutionLock.ExitWriteLock();
            _activeExecutionLock.Dispose();
        }
    }
}
