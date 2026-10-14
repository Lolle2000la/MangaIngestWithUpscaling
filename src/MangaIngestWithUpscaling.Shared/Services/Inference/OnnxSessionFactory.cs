using System.Collections.Concurrent;
using System.Globalization;
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

    /// <summary>
    /// Set by a provider probe that failed without an absence verdict (see the CUDA probe's broad
    /// catch), and read by <see cref="GetEffectiveBackend"/> to decide whether the resolution it
    /// produced is final or merely what a not-yet-ready driver left. A resolution that skipped a
    /// higher-priority candidate for such a reason is provisional whether it landed on CPU or on a
    /// lower-priority accelerator. Both sides go through <see cref="_backendGate"/>:
    /// <see cref="GetEffectiveBackend"/> resets and reads it under the gate, and
    /// <see cref="MarkProbeTransient"/> sets it under the same gate, so a probe on a session-creation
    /// thread cannot land between that reset and read and make an unrelated resolution provisional.
    /// </summary>
    private bool _probeWasTransient;

    /// <summary>
    /// Records that a provider probe failed without an absence verdict, so the resolution it belongs
    /// to is retried rather than latched. Taken under <see cref="_backendGate"/> to serialize with
    /// <see cref="GetEffectiveBackend"/>'s reset-and-read window. The lock is reentrant, so the
    /// probes reached from a resolution that already holds it (via
    /// <see cref="ResolveEffectiveBackend"/>) can call this too.
    /// </summary>
    private void MarkProbeTransient()
    {
        lock (_backendGate)
        {
            _probeWasTransient = true;
        }
    }

    /// <summary>
    /// Whether the cached resolution was produced while some higher-priority provider was skipped for
    /// a transient reason (or the resolution itself failed transiently and fell back to CPU). Such a
    /// resolution is retried on <see cref="BackendTransientRetryInterval"/> rather than latched.
    /// </summary>
    private bool _backendResolutionWasTransient;

    private DateTime _lastBackendResolutionUtc = DateTime.MinValue;

    /// <summary>
    /// How long a resolution produced under a transient probe failure stays cached before the probe is
    /// retried. Long enough that a persistently failing provider does not re-probe on every call (the
    /// resolution is real work, and it once ran once per page), short enough that a driver that
    /// becomes ready after boot is picked up without a process restart.
    /// </summary>
    private static readonly TimeSpan BackendTransientRetryInterval = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);
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
    private volatile bool _disposed;

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
                RemoveFromLru(fullPath);
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
                RemoveFromLru(fullPath);
                RetireLocked(session);
            }
        }
    }

    /// <inheritdoc />
    public bool IsSessionCached(string modelPath)
    {
        if (_disposed)
        {
            return false;
        }

        string fullPath = Path.GetFullPath(modelPath);
        lock (_lock)
        {
            return _sessions.ContainsKey(fullPath);
        }
    }

    /// <summary>
    /// Removes <paramref name="fullPath"/> from the LRU list.
    /// <para>
    /// Ordinal, matching <see cref="_sessions"/>: both are keyed by the same full paths, so an ordinal
    /// removal finds the entry the cache holds. A case-insensitive match would remove the wrong node on
    /// a case-sensitive filesystem, where two paths differing only in case are different models — the
    /// list would lose an entry the cache still has, and the eviction order would no longer describe
    /// the sessions it exists to order.
    /// </para>
    /// </summary>
    private void RemoveFromLru(string fullPath)
    {
        for (LinkedListNode<string>? node = _lruOrder.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, fullPath, StringComparison.Ordinal))
            {
                _lruOrder.Remove(node);
                return;
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

        // The arena settings are deliberately NOT session config entries. "arena_extend_strategy" is a
        // CUDA provider option and "memory.enable_memory_arena_shrinkage" is a RunOptions key; ONNX
        // Runtime reads neither from SessionOptions, and AddSessionConfigEntry accepts unknown keys, so
        // setting them here was a silent no-op (verified against ORT 1.29: inference_session.cc never
        // looks either up). The extension strategy is passed to the CUDA provider below; the shrinkage
        // is applied where a RunOptions is already in play (SplitDetectionService), and the upscaler
        // does not need it at all — it releases the whole session at the chapter boundary and whenever
        // the tile shape changes, which frees the arena outright rather than shrinking it.

        if (forceCpu)
        {
            logger.LogInformation(
                "Creating ONNX session for {Model} using CPU execution provider.",
                Path.GetFileName(modelPath)
            );
            return new InferenceSession(modelPath, options);
        }

        // Attach the provider GetEffectiveBackend resolved, rather than probing Auto again here. The
        // engine identity, the calibration fingerprint and the fp16 choice all describe that one
        // resolution; a second, independent probe let a session run on a provider the resolution had
        // rejected or had not yet accepted, so pages could be produced by one backend under another's
        // identity and blended into one chapter.
        GpuBackend effectiveBackend = GetEffectiveBackend();
        bool gpuConfigured =
            effectiveBackend != GpuBackend.CPU
            && TryConfigureGpuProvider(
                options,
                effectiveBackend,
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

    /// <summary>
    /// The ONNX Runtime status text that means a provider is definitively absent rather than merely
    /// not ready: it is not compiled into this build, or its provider shared library cannot be
    /// loaded. Both are permanent for the process, so a probe that sees one must latch the negative
    /// availability instead of being re-probed on <see cref="BackendTransientRetryInterval"/>.
    /// <para>
    /// This is needed because the CUDA probe is the one provider ORT reports through a return status
    /// rather than a missing export: the shipped CPU build exposes the OrtApi entry point (so no
    /// <see cref="EntryPointNotFoundException"/>) and answers
    /// "CUDA execution provider is not enabled in this build." — which, classed as an ordinary
    /// failure, left the provider re-probed once a minute for the process lifetime. Deliberately
    /// narrow: every other status, including a driver that is not ready yet, stays transient.
    /// </para>
    /// </summary>
    internal static bool IsAbsentProviderStatus(string message) =>
        message.Contains("not enabled in this build", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Failed to load shared library", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attaches the provider <see cref="GetEffectiveBackend"/> resolved to the session options.
    /// <para>
    /// The backend is already concrete and normalised: <see cref="ResolveEffectiveBackend"/> ran the
    /// Auto discovery and collapsed the legacy aliases (ROCm/XPU to WebGPU, CUDA_12_8 to CUDA), so it
    /// is never <see cref="GpuBackend.Auto"/>. Re-running that discovery here is what let the session
    /// and the advertised backend diverge.
    /// </para>
    /// </summary>
    private bool TryConfigureGpuProvider(
        SessionOptions options,
        GpuBackend backend,
        int deviceId,
        string modelName
    ) =>
        backend switch
        {
            GpuBackend.WebGPU => TryConfigureWebGpu(options, deviceId, modelName),
            GpuBackend.CUDA => TryConfigureCuda(options, deviceId, modelName),
            GpuBackend.DirectML => TryConfigureDirectML(options, deviceId, modelName),
            GpuBackend.OpenVINO => TryConfigureOpenVino(options, deviceId, modelName),
            GpuBackend.MIGraphX => TryConfigureMIGraphX(options, deviceId, modelName),
            // CPU attaches nothing; anything else (Auto) cannot reach here through ResolveEffectiveBackend.
            _ => false,
        };

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

            // The BFC arena's default extension strategy doubles each allocation, so a run can reserve
            // far more VRAM than it asks for. kSameAsRequested allocates what was requested; it is a
            // provider option, which is why it is passed here and not through
            // SessionOptions.AddSessionConfigEntry (see CreateSession).
            using var cudaOptions = new OrtCUDAProviderOptions();
            cudaOptions.UpdateOptions(
                new Dictionary<string, string>
                {
                    ["device_id"] = deviceId.ToString(CultureInfo.InvariantCulture),
                    ["arena_extend_strategy"] = "kSameAsRequested",
                }
            );
            options.AppendExecutionProvider_CUDA(cudaOptions);
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
            when (ex is OnnxRuntimeException onnx && IsAbsentProviderStatus(onnx.Message))
        {
            // The CPU build answers through the OrtApi entry point rather than a missing export, so
            // "not enabled in this build" arrives here as an OnnxRuntimeException; a build that has
            // the EP but not its provider library reports the same way with "Failed to load shared
            // library". Neither changes while the process runs, so it latches.
            logger.LogDebug(
                ex,
                "CUDA execution provider is absent from this ONNX Runtime build or host."
            );
            _cudaAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append CUDA execution provider. Will try next provider or CPU fallback."
            );
            // Not latched: unlike a missing entry point or library, this failure does not say the
            // provider is absent — a driver that is not ready yet at boot produces it, and the next
            // session creation re-probes and can attach the provider once it is. Latching here is
            // what pinned a boot-time blip to CPU for the whole process.
            MarkProbeTransient();
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
            when (ex is OnnxRuntimeException onnx && IsAbsentProviderStatus(onnx.Message))
        {
            // A provider compiled in but whose shared library cannot be loaded reports this way; a
            // missing export is the EntryPointNotFoundException above. Both are permanent.
            logger.LogDebug(
                ex,
                "OpenVINO execution provider is absent from this ONNX Runtime build or host."
            );
            _openVinoAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append OpenVINO execution provider. Will try next provider or CPU fallback."
            );
            // Not latched — see the CUDA probe for why a plain failure is not an absence verdict.
            MarkProbeTransient();
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
            when (ex is OnnxRuntimeException onnx && IsAbsentProviderStatus(onnx.Message))
        {
            // A provider compiled in but whose shared library cannot be loaded reports this way; a
            // missing export is the EntryPointNotFoundException above. Both are permanent.
            logger.LogDebug(
                ex,
                "MIGraphX execution provider is absent from this ONNX Runtime build or host."
            );
            _migraphxAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append MIGraphX execution provider. Will try next provider or CPU fallback."
            );
            // Not latched — see the CUDA probe for why a plain failure is not an absence verdict.
            MarkProbeTransient();
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
        catch (EntryPointNotFoundException)
        {
            logger.LogDebug(
                "DirectML execution provider entry point not found in this ONNX Runtime build."
            );
            _directMlAvailable = false;
            return false;
        }
        catch (DllNotFoundException)
        {
            logger.LogDebug("DirectML runtime libraries not found on this system.");
            _directMlAvailable = false;
            return false;
        }
        catch (Exception ex)
            when (ex is OnnxRuntimeException onnx && IsAbsentProviderStatus(onnx.Message))
        {
            // A provider compiled in but whose shared library cannot be loaded reports this way; a
            // missing export is the EntryPointNotFoundException above. Both are permanent.
            logger.LogDebug(
                ex,
                "DirectML execution provider is absent from this ONNX Runtime build or host."
            );
            _directMlAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to append DirectML execution provider. Will try CPU fallback."
            );
            // Not latched — see the CUDA probe for why a plain failure is not an absence verdict.
            MarkProbeTransient();
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
                // Unknown rather than absent: the device list can be empty because the driver is
                // not ready yet. Marking it lets a CPU resolution it caused be retried instead of
                // latched — this probe has no negative availability flag, so a later session
                // creation re-probes anyway and could attach WebGPU while GetEffectiveBackend()
                // still said CPU.
                MarkProbeTransient();
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
            // Not latched — see the CUDA probe for why a plain failure is not an absence verdict.
            // This was the one probe the un-latching missed, so a WebGPU failure latched CPU for
            // the process while CreateSession kept re-probing and could attach GPU sessions under
            // the CPU identity.
            MarkProbeTransient();
            return false;
        }
    }

    public GpuBackend GetEffectiveBackend()
    {
        // Cached once the answer is final. The probe below builds a SessionOptions and asks the
        // runtime to attach each candidate provider, which is real work — and it once ran on every
        // call, i.e. once per page through the calibrator's fingerprint. The answer is final while
        // the configuration is a fixed snapshot (IOptions, not IOptionsMonitor) and neither the
        // accelerator nor the runtime changes underneath it — except when a probe failed without an
        // absence verdict (a driver not ready at boot). Such a resolution is not an answer: on the
        // Auto path it can have skipped a higher-priority provider (CUDA blips, so WebGPU is
        // resolved), so it is provisional whether it landed on CPU or on a lower provider. Retrying
        // it on the interval below keeps the engine identity, the calibration fingerprint and the
        // fp16 resolution off a backend that nothing but bad timing chose, and — because
        // CreateSession attaches this same resolution rather than probing again — keeps the backend a
        // session runs on equal to the one they advertise. A mid-process change resets the spool of
        // the task being streamed, which is correct: the pages really would have been upscaled by a
        // different backend.
        lock (_backendGate)
        {
            if (
                _resolvedBackend is { } resolved
                && (
                    !_backendResolutionWasTransient
                    || DateTime.UtcNow - _lastBackendResolutionUtc < BackendTransientRetryInterval
                )
            )
            {
                return resolved;
            }

            _probeWasTransient = false;
            GpuBackend backend = ResolveEffectiveBackend();
            _lastBackendResolutionUtc = DateTime.UtcNow;
            // Provisional whenever any candidate was skipped for a transient reason, not only when
            // the fallback reached CPU: the skipped provider can recover, and latching the lower one
            // would pin both the advertised backend and (now) the session to it for the process.
            _backendResolutionWasTransient = _probeWasTransient;
            _resolvedBackend = backend;
            return backend;
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
            // The preference is not a promise: appending an explicit provider can fail (a missing
            // runtime, an unsupported device) and CreateSession then silently builds a CPU session.
            // Returning the preference unverified would let the engine identity, the calibration
            // fingerprint and the logs name a backend that is not the one running — the opposite of
            // what EngineIdentity documents. The same append the Auto path probes decides it here.
            return CanAttach(backend, Math.Max(0, currentConfig.SelectedDeviceIndex - 1))
                ? backend
                : GpuBackend.CPU;
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

    /// <summary>
    /// Whether an explicitly configured provider can actually be attached on this host, by performing
    /// the same append <see cref="CreateSession"/> would. Unlike the Auto path, a failure here is not
    /// allowed to fall through to a provider the operator did not ask for: the session falls back to
    /// CPU, so this reports CPU too.
    /// <para>
    /// The append is what can be checked without building a session. A provider that attaches but
    /// still fails at session construction (an operator or graph the EP rejects) falls back to CPU in
    /// <see cref="CreateSession"/> and is not distinguished here.
    /// </para>
    /// </summary>
    private bool CanAttach(GpuBackend backend, int deviceId)
    {
        using var testOptions = new SessionOptions();
        return backend switch
        {
            GpuBackend.WebGPU => TryConfigureWebGpu(testOptions, deviceId, "probe"),
            GpuBackend.CUDA => TryConfigureCuda(testOptions, deviceId, "probe"),
            GpuBackend.DirectML => TryConfigureDirectML(testOptions, deviceId, "probe"),
            GpuBackend.OpenVINO => TryConfigureOpenVino(testOptions, deviceId, "probe"),
            GpuBackend.MIGraphX => TryConfigureMIGraphX(testOptions, deviceId, "probe"),
            // CPU attaches nothing; it is also what a failed append falls back to.
            _ => backend == GpuBackend.CPU,
        };
    }

    public void Dispose()
    {
        // Compare-exchanged, not checked-then-set: two threads disposing concurrently (the DI
        // container and a shutdown path) could both pass a plain check and both run the teardown,
        // double-entering the write lock below. Readers keep the plain read — the worst a torn
        // read costs them is one throw on an operation that was racing disposal anyway.
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

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
