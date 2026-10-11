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
    private readonly Lock _backendGate = new();
    private GpuBackend? _resolvedBackend;
    private readonly Dictionary<string, InferenceSession> _sessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly LinkedList<string> _lruOrder = new();

    /// <summary>
    /// Sessions removed from the cache (evicted or invalidated) that a caller may still be running
    /// under an inference scope. They are disposed by <see cref="DrainRetired"/>, which takes the
    /// write lock and therefore waits for every scope to drain, so a caller can never Run a session
    /// this factory has already freed.
    /// <para>
    /// Eviction cannot dispose inline: it happens while the evicting caller holds its own read scope
    /// (the fetch is taken inside the scope, see <see cref="AcquireSessionOrFetch"/> docs on call
    /// sites), and taking the write lock from inside a read scope would deadlock the same thread.
    /// </para>
    /// </summary>
    private readonly HashSet<InferenceSession> _retired = new(ReferenceEqualityComparer.Instance);
    private int _drainScheduled;

    private readonly ReaderWriterLockSlim _activeExecutionLock = new(
        LockRecursionPolicy.SupportsRecursion
    );
    public const int MaxCachedSessions = 1;
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

                    // Retired rather than disposed here: this runs while the caller holds its read
                    // scope, and the write lock a synchronous dispose needs would deadlock the same
                    // thread.
                    RetireLocked(oldSession);
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

        lock (_lock)
        {
            if (_disposed)
                return;
            if (_sessions.Remove(fullPath, out var session))
            {
                _lruOrder.Remove(fullPath);
                RetireLocked(session);
            }
        }
    }

    /// <summary>
    /// Removes a session from the cache's ownership and disposes it once no inference scope holds it.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private void RetireLocked(InferenceSession session)
    {
        _retired.Add(session);
        ScheduleRetiredDrain();
    }

    private void ScheduleRetiredDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state => ((OnnxSessionFactory)state!).DrainRetired(),
            this
        );
    }

    private void DrainRetired()
    {
        while (true)
        {
            List<InferenceSession> batch;
            lock (_lock)
            {
                if (_retired.Count == 0)
                {
                    Volatile.Write(ref _drainScheduled, 0);
                    return;
                }

                batch = [.. _retired];
                _retired.Clear();
            }

            // The write lock waits for every read scope to drain, so nothing can be Running these.
            try
            {
                _activeExecutionLock.EnterWriteLock();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() won a shutdown race: it disposed _activeExecutionLock, and it only does
                // that after it has taken the write lock itself, so every inference scope has
                // already ended and no new one can start (_disposed is set). These sessions were
                // removed from _retired by the snapshot above, so Dispose()'s own snapshot did not
                // see them; free them here, with no scope possible, instead of leaking the native
                // handles — and never surface an unhandled ObjectDisposedException on a pool thread.
                foreach (var session in batch)
                {
                    DisposeSession(session, "retired during factory disposal");
                }

                return;
            }

            try
            {
                foreach (var session in batch)
                {
                    DisposeSession(session, "retired");
                }
            }
            finally
            {
                _activeExecutionLock.ExitWriteLock();
            }
        }
    }

    private void DisposeSession(InferenceSession session, string context)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanly dispose {Context} session", context);
        }
    }

    public void InvalidateAllSessions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_disposed)
                return;

            foreach (var session in _sessions.Values)
            {
                RetireLocked(session);
            }

            _sessions.Clear();
            _lruOrder.Clear();
        }
    }

    /// <inheritdoc />
    public void ReleaseCachedSessions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<InferenceSession> batch;
        lock (_lock)
        {
            if (_disposed)
                return;

            batch = [.. _sessions.Values, .. _retired];
            _sessions.Clear();
            _lruOrder.Clear();
            _retired.Clear();
        }

        // The interface contract requires that the caller holds no inference scope, so taking the
        // write lock here waits out only scopes on other threads and frees the sessions on this
        // thread before returning. A pending background drain just finds _retired empty afterwards.
        try
        {
            _activeExecutionLock.EnterWriteLock();
        }
        catch (ObjectDisposedException)
        {
            // Dispose() raced this flush during shutdown. It takes the write lock before disposing
            // the lock, so every scope has ended and none can start; free the batch here, with no
            // scope possible, rather than throw or leak it.
            foreach (var session in batch)
            {
                DisposeSession(session, "released during factory disposal");
            }

            return;
        }

        try
        {
            foreach (var session in batch)
            {
                DisposeSession(session, "released");
            }
        }
        finally
        {
            _activeExecutionLock.ExitWriteLock();
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
        bool forceCpu = ShouldUseCpuProvider(currentConfig);

        // Disposed on the way out: InferenceSession reads the options when it is constructed, and
        // SessionOptions is a SafeHandle, so leaving it to the finalizer only defers the native free.
        using SessionOptions options = new()
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
            using SessionOptions cpuOptions = new()
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

    /// <summary>
    /// Decides whether the session is built CPU-only, without ever trying a GPU provider.
    /// <para>
    /// <see cref="GpuBackend.CPU"/> is a first-class choice and <see cref="GetEffectiveBackend"/>
    /// reports it as CPU, so it has to reach the CPU path here too. Without the explicit case it
    /// matched no branch in <see cref="TryConfigureGpuProvider"/> and fell through to the Auto
    /// discovery block, which attaches CUDA or WebGPU — the opposite of what the setting is
    /// recommended for (running the models the GPU EP mis-executes), while the engine identity and
    /// the logs still said CPU.
    /// </para>
    /// </summary>
    internal static bool ShouldUseCpuProvider(UpscalerConfig currentConfig) =>
        currentConfig.UseCPU
        || currentConfig.SelectedDeviceIndex <= 0
        || currentConfig.PreferredGpuBackend == GpuBackend.CPU;

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
            logger.LogDebug(
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
            logger.LogDebug(
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
            logger.LogDebug(
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
            logger.LogDebug(
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

            logger.LogDebug(
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
        // Resolved once per process. The probe below builds a SessionOptions and asks the runtime to
        // attach each candidate provider, which is real work — and it ran on every call, i.e. once
        // per page through the calibrator's fingerprint. The answer cannot change while the process
        // runs: the configuration is a fixed snapshot (IOptions, not IOptionsMonitor) and neither
        // the accelerator nor the runtime changes underneath it.
        lock (_backendGate)
        {
            return _resolvedBackend ??= ResolveEffectiveBackend();
        }
    }

    private GpuBackend ResolveEffectiveBackend()
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
            sessionsToDispose = [.. _sessions.Values, .. _retired];
            _sessions.Clear();
            _lruOrder.Clear();
            _retired.Clear();
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
