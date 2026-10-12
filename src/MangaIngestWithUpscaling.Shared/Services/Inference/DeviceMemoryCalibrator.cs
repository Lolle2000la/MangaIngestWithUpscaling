using System.Globalization;
using System.Reflection;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Measures how much device memory a real inference actually costs on the configured accelerator,
/// so tile-size estimation can use that instead of built-in coefficients.
/// </summary>
public interface IDeviceMemoryCalibrator
{
    /// <summary>
    /// Returns the calibration for the current device, running the benchmark once with a small
    /// representative model if no valid profile exists yet. Never throws: a failed benchmark falls
    /// back to the built-in coefficients. This is the device-wide default; prefer
    /// <see cref="GetOrCalibrateForModelAsync"/> where the model is known.
    /// </summary>
    Task<DeviceMemoryProfile?> GetOrCalibrateAsync(
        int deviceId,
        string modelsDirectory,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns the calibration for the current device with a measurement for
    /// <paramref name="modelPath"/> filled in, running that model's benchmark once if it has none.
    /// Never throws: a failed benchmark leaves the device-wide default (or the built-ins) in place.
    /// <para>
    /// The per-pixel cost belongs to the model and the execution provider together, so this is the
    /// accurate entry point. Measured on an RX 9070 XT / WebGPU EP: fp16 ESRGAN 0.90x its analytic
    /// working set, fp32 ESRGAN 0.69x, fp32 FDAT_M 1.28x. A device-wide factor cannot represent
    /// that range, and using one under-estimated the fp32 transformer enough to spill into GTT.
    /// </para>
    /// </summary>
    Task<DeviceMemoryProfile?> GetOrCalibrateForModelAsync(
        int deviceId,
        string modelsDirectory,
        string modelPath,
        int scale,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Runs a short inference benchmark on the GPU to derive the two numbers that cannot be read off
/// the model graph:
/// <list type="bullet">
/// <item><see cref="ModelMemoryProfile.ActivationScale"/> — how many times the analytically
/// derived activation working set the execution provider really needs (precision casts, layout
/// re-packing, non-shrinking arenas)</item>
/// <item><see cref="DeviceMemoryProfile.SessionReservationBytes"/> — how much device memory a live
/// session holds independently of tile size</item>
/// </list>
/// Two tile sizes give two measurements, and their difference isolates the per-pixel term:
/// <c>peak(n) = reservation + scale · analytic · n²</c>. The measurement is per model, because the
/// scale is a property of the model and the provider together rather than of the device.
/// </summary>
[RegisterSingleton]
public sealed class DeviceMemoryCalibrator(
    IOnnxSessionFactory sessionFactory,
    IOptions<UpscalerConfig> config,
    DeviceMemoryProfileStore store,
    ILogger<DeviceMemoryCalibrator> logger
) : IDeviceMemoryCalibrator
{
    /// <summary>
    /// Bumped whenever the built-in coefficients or the measurement method change, so a profile
    /// stored by an older build is re-measured instead of trusted. v3 measures each benchmark tile
    /// on its own session (see <see cref="MeasurePoint"/>) and records the result per model.
    /// </summary>
    public const int CalibrationVersion = 3;

    private static readonly Lock BenchmarkGate = new();
    private readonly SemaphoreSlim asyncGate = new(1, 1);

    /// <summary>
    /// Fingerprints the benchmark has already been attempted for and failed on, so it is not
    /// retried for every page. Without this, a models directory the profile cannot be written to
    /// (a read-only volume, a container running as an arbitrary uid) makes
    /// <see cref="GetOrCalibrateAsync"/> return null forever — and every page pays inferences plus a
    /// session teardown to reach it. Scoped to the process: a worker that skips a chapter because of
    /// it re-benchmarks on the next. Entries are keyed by device fingerprint, or by device
    /// fingerprint plus model for the per-model path.
    /// </summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the forced recalibration has already been honoured in this process.
    /// <see cref="UpscalerConfig.RecalibrateDeviceMemory"/> means "on the next upscale", but
    /// configuration is only ever read — nothing writes <see cref="UpscalerConfig"/> back — so the
    /// flag cannot be cleared at the source. Remembering the one-shot here is what stops it from
    /// re-benchmarking on every page of every chapter.
    /// </summary>
    private bool _forcedRecalibrationDone;

    public async Task<DeviceMemoryProfile?> GetOrCalibrateAsync(
        int deviceId,
        string modelsDirectory,
        CancellationToken cancellationToken = default
    ) => await GetOrCalibrateCoreAsync(deviceId, modelsDirectory, null, 0, cancellationToken);

    public async Task<DeviceMemoryProfile?> GetOrCalibrateForModelAsync(
        int deviceId,
        string modelsDirectory,
        string modelPath,
        int scale,
        CancellationToken cancellationToken = default
    ) =>
        await GetOrCalibrateCoreAsync(
            deviceId,
            modelsDirectory,
            modelPath,
            scale,
            cancellationToken
        );

    private async Task<DeviceMemoryProfile?> GetOrCalibrateCoreAsync(
        int deviceId,
        string modelsDirectory,
        string? modelPath,
        int scale,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return null;
        }

        string fingerprint = BuildFingerprint(deviceId);
        if (fingerprint.Length == 0)
        {
            return null;
        }

        await asyncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool force = config.Value.RecalibrateDeviceMemory && !_forcedRecalibrationDone;

            // Loaded whether or not this is a forced run. Force re-measures the requested key; it must
            // not discard the rest of the profile, because the store replaces the whole device entry
            // on save — starting from an empty skeleton dropped every other model's measurement (and
            // the device default) for the rest of the process.
            DeviceMemoryProfile? cached = store.Load(modelsDirectory, fingerprint);

            // CPU mode has no per-tile footprint worth calibrating: the activation working set is
            // allocated in system RAM, not on a device with a budget we are trying to fill.
            if (config.Value.UseCPU)
            {
                return cached;
            }

            string? modelKey = modelPath is null
                ? null
                : DeviceMemoryProfile.ModelKey(modelPath, FileLength(modelPath));

            // Nothing to measure: the profile is cached and, when a model was named, it already
            // carries that model's own measurement. Returning the stored profile untouched is what
            // keeps a cached device from touching the GPU at all. Skipped on a forced run, which is
            // asking for exactly the measurement this check would short-circuit.
            if (
                !force
                && cached is not null
                && (modelKey is null || cached.Models.ContainsKey(modelKey))
            )
            {
                return cached;
            }

            string attemptedKey = modelKey is null ? fingerprint : fingerprint + "|" + modelKey;

            // The benchmark already came back empty for this key in this process, so there is nothing
            // to learn by trying again: a models directory the profile cannot be written back to
            // would otherwise pay the benchmark on every page. A measurement that worked is
            // deliberately not in here.
            if (!force && _unsupported.Contains(attemptedKey))
            {
                return cached;
            }

            DeviceMemoryProfile profile = cached ?? CreateProfileSkeleton(fingerprint, deviceId);

            bool measured = false;
            try
            {
                measured = modelPath is null
                    ? MeasureDeviceDefault(deviceId, modelsDirectory, profile)
                    : MeasureModel(deviceId, modelsDirectory, profile, modelPath, scale);

                // Once the forced benchmark has actually run, the forced calibration is spent:
                // whether it produced a profile or not, every later call takes the normal path.
                _forcedRecalibrationDone = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The benchmark ran and threw, so the calibration is still spent. The failure is
                // remembered unconditionally: a forced attempt has to be as sticky as a normal one,
                // otherwise the next page falls straight through to a second benchmark.
                _forcedRecalibrationDone = true;
                logger.LogWarning(
                    ex,
                    "Device memory benchmark failed; using built-in coefficients"
                );
            }

            if (!measured)
            {
                // Nothing usable came back — an implausible reading, a model that cannot be probed,
                // or no calibration model at all. Remember it so every page does not pay the
                // benchmark again. A measurement that worked is deliberately not recorded here,
                // which is what stops a models directory the profile cannot be written back to from
                // discarding a measurement that did work.
                _unsupported.Add(attemptedKey);
                return cached;
            }

            return profile;
        }
        finally
        {
            asyncGate.Release();
        }
    }

    /// <summary>
    /// A profile with the device identity filled in but no measurement yet.
    /// </summary>
    private static DeviceMemoryProfile CreateProfileSkeleton(string fingerprint, int deviceId) =>
        new()
        {
            Fingerprint = fingerprint,
            Provider = ExecutionProvider.Unknown,
            DeviceName = ResolveDeviceName(deviceId),
            TotalDeviceMemoryBytes = ResolveTotalDeviceMemory(deviceId),
            CalibratedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Measures the device-wide default with a small representative model. Kept as the fallback for
    /// a model whose own benchmark fails.
    /// </summary>
    private bool MeasureDeviceDefault(
        int deviceId,
        string modelsDirectory,
        DeviceMemoryProfile profile
    )
    {
        string? modelPath = SelectCalibrationModel(modelsDirectory, ConfiguredBudget(deviceId));
        if (modelPath is null)
        {
            logger.LogDebug(
                "No model small enough to benchmark device memory with; using built-in coefficients"
            );
            return false;
        }

        int scale = OnnxUpscaleEngine.InferScaleFromModelName(Path.GetFileName(modelPath));
        long budget = ConfiguredBudget(deviceId);

        ModelMemoryProfile? measured = RunBenchmark(deviceId, modelPath, scale, budget);
        if (measured is null)
        {
            return false;
        }

        profile.Provider = ToProvider(sessionFactory.GetEffectiveBackend());
        profile.ActivationScale = measured.ActivationScale;
        profile.SessionReservationBytes = measured.SessionReservationBytes;
        profile.CalibratedAtUtc = measured.CalibratedAtUtc;
        profile.CalibratedWithModel = Path.GetFileName(modelPath);
        profile.MeasuredTiles = measured.MeasuredTiles;
        profile.MeasuredPeakBytes = measured.MeasuredPeakBytes;
        store.Save(modelsDirectory, profile);

        logger.LogInformation(
            "Calibrated device memory profile for {Device} on {Provider}: activation scale {Scale:F2}x, session reservation {Reservation} MB (measured at {Small}px and {Large}px tiles with {Model})",
            profile.DeviceName,
            profile.Provider,
            profile.ActivationScale,
            profile.SessionReservationBytes / 1048576,
            measured.MeasuredTiles[0],
            measured.MeasuredTiles[1],
            profile.CalibratedWithModel
        );
        return true;
    }

    /// <summary>
    /// Measures <paramref name="modelPath"/> on this device and records it on the profile.
    /// </summary>
    private bool MeasureModel(
        int deviceId,
        string modelsDirectory,
        DeviceMemoryProfile profile,
        string modelPath,
        int scale
    )
    {
        long budget = ConfiguredBudget(deviceId);
        ModelMemoryProfile? measured = RunBenchmark(deviceId, modelPath, scale, budget);
        if (measured is null)
        {
            return false;
        }

        profile.Provider = ToProvider(sessionFactory.GetEffectiveBackend());
        profile.Models[DeviceMemoryProfile.ModelKey(modelPath, FileLength(modelPath))] = measured;
        store.Save(modelsDirectory, profile);

        logger.LogInformation(
            "Calibrated {Model} on {Device} ({Provider}): activation scale {Scale:F2}x (measured at {Small}px and {Large}px tiles)",
            Path.GetFileName(modelPath),
            profile.DeviceName,
            profile.Provider,
            measured.ActivationScale,
            measured.MeasuredTiles[0],
            measured.MeasuredTiles[1]
        );
        return true;
    }

    /// <summary>
    /// Identity of the measured configuration. The execution provider comes from the session factory
    /// (the single place that resolves it, honouring <see cref="UpscalerConfig.PreferredGpuBackend"/>,
    /// <see cref="UpscalerConfig.UseCPU"/> and the device index), so a calibration is only ever
    /// reused for the provider that will actually run the model. The device is part of the key, so a
    /// second GPU, or the same GPU under CUDA instead of WebGPU, gets its own measurement.
    /// </summary>
    public string BuildFingerprint(int deviceId)
    {
        GpuBackend backend = sessionFactory.GetEffectiveBackend();
        string provider = backend.ToString().ToLowerInvariant();

        string deviceName = "unknown";
        long totalBytes = 0;
        try
        {
            if (
                VulkanMemoryProvider.IsAvailable
                && VulkanMemoryProvider.DeviceCount > deviceId
                && VulkanMemoryProvider.QueryDevice(deviceId) is VulkanGpuMemory vk
            )
            {
                deviceName = vk.DeviceName;
                totalBytes = vk.TotalVramBytes;
            }

            if (totalBytes == 0)
            {
                var (total, _, _) = OnnxTiler.GetGpuVramInfo(deviceId);
                totalBytes = total;
            }
        }
        catch
        {
            // A fingerprint with unknown device details still works, it just matches less often
        }

        string runtimeVersion =
            typeof(Microsoft.ML.OnnxRuntime.InferenceSession)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0";
        // Trim the source-hash suffix MSBuild appends to informational versions
        int plus = runtimeVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            runtimeVersion = runtimeVersion[..plus];
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{provider}|{deviceId}|{deviceName}|{totalBytes}|{runtimeVersion}|v{CalibrationVersion}"
        );
    }

    /// <summary>
    /// Fits <c>peak(n) = reservation + scale · analytic · n²</c> for one model from two tile sizes,
    /// or null when the measurement is unusable.
    /// </summary>
    private ModelMemoryProfile? RunBenchmark(int deviceId, string modelPath, int scale, long budget)
    {
        // Never run the benchmark concurrently with a real upscale: both would need the device.
        lock (BenchmarkGate)
        {
            long fileBytes = FileLength(modelPath);
            OnnxTiler.ActivationCoefficients analytic = OnnxTiler.ActivationCoefficients.For(
                modelPath,
                fileBytes
            );

            bool isFp16;
            try
            {
                isFp16 = IsFp16Model(modelPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Could not read the input type of {Model} to benchmark it",
                    Path.GetFileName(modelPath)
                );
                return null;
            }

            int large = ChooseLargeTile(scale, isFp16, analytic, budget);
            int small = Math.Max(96, (large / 3 / 64) * 64);
            if (small >= large)
            {
                return null;
            }

            long growthSmall = MeasurePoint(deviceId, modelPath, small, scale);
            long growthLarge = MeasurePoint(deviceId, modelPath, large, scale);

            double analyticBytesPerPixel = analytic.BytesPerPixel(scale, isFp16);
            double bytesPerPixel =
                (growthLarge - growthSmall) / (double)((long)large * large - (long)small * small);
            double activationScale =
                analyticBytesPerPixel > 0 ? bytesPerPixel / analyticBytesPerPixel : 1.0;

            // Sanity: a factor far outside the plausible range means the measurement was distorted
            // (another process on the device, driver eviction, ...). Keep built-ins.
            if (activationScale is < 0.2 or > 12.0)
            {
                logger.LogWarning(
                    "Device memory benchmark for {Model} produced an implausible result (activationScale={Scale:F2}); keeping built-in coefficients",
                    Path.GetFileName(modelPath),
                    activationScale
                );
                return null;
            }

            // The two measured growths include the model weights and the session's own arena; the
            // fit separates them. The reservation is what the planner needs *besides* the weights it
            // adds itself, so the weights come off here rather than being counted twice.
            long weights = OnnxTiler.EstimateModelBytes(modelPath, fileBytes);
            long reservation = Math.Max(
                0,
                growthLarge - (long)(bytesPerPixel * large * large) - weights
            );
            if (reservation > 8L * 1024 * 1024 * 1024)
            {
                reservation = 0;
            }

            return new ModelMemoryProfile
            {
                ActivationScale = activationScale,
                SessionReservationBytes = reservation,
                MeasuredTiles = [small, large],
                MeasuredPeakBytes = [growthSmall, growthLarge],
                CalibratedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private bool IsFp16Model(string modelPath)
    {
        using (sessionFactory.EnterInferenceScope())
        {
            InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);
            return session.InputMetadata.TryGetValue("input", out var meta)
                && meta.ElementType == typeof(Float16);
        }
    }

    /// <summary>
    /// Runs one inference on an n×n tile through the engine's real tiling path and returns the
    /// device-memory growth over the pre-session baseline.
    /// <para>
    /// Each point gets its own freshly created session. The WebGPU EP caches its buffers per tensor
    /// shape and exposes no way to release them, so two sizes measured in one session make the
    /// larger reading include the smaller one's cached buffers: on an RX 9070 XT that inflated the
    /// fitted slope from 19 140 to 21 465 B/px (+12%), and the tile plan with it. The cached session
    /// is released synchronously (not retired) so the next baseline is read with the previous
    /// session genuinely gone.
    /// </para>
    /// </summary>
    private long MeasurePoint(int deviceId, string modelPath, int n, int scale)
    {
        sessionFactory.ReleaseCachedSessions();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long baseline = DeviceMemorySampler.Read(deviceId);

        long peak;
        using (sessionFactory.EnterInferenceScope())
        {
            InferenceSession session = sessionFactory.GetOrCreateSession(modelPath);
            peak = MeasureTile(session, deviceId, n, scale);
        }

        sessionFactory.ReleaseCachedSessions();
        return Math.Max(0, peak - baseline);
    }

    /// <summary>
    /// Runs one inference on an n×n tile through the engine's real tiling path and returns the peak
    /// device memory observed while it ran.
    /// </summary>
    private static long MeasureTile(
        Microsoft.ML.OnnxRuntime.InferenceSession session,
        int deviceId,
        int n,
        int scale
    )
    {
        byte[] input = new byte[n * n * 3];
        Random.Shared.NextBytes(input);
        return DeviceMemorySampler.Measure(
            deviceId,
            () =>
                OnnxTiler.UpscaleTile(input, n, n, scale, session, default, targetW: n, targetH: n)
        );
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The same budget the engine plans tiles against: the operator's override when one is set,
    /// otherwise the driver's number with the configured margin and exclusivity threshold. Benchmarking
    /// against the bare driver budget meant a deliberately small <c>MemoryBudgetBytes</c> was ignored
    /// while the benchmark chose its tile sizes, so it could measure — and then store — tiles the
    /// planner would never allow, on a device the operator had capped.
    /// </summary>
    private long ConfiguredBudget(int deviceId)
    {
        UpscalerConfig current = config.Value;
        return current.ResolvedMemoryBudgetBytes > 0
            ? current.ResolvedMemoryBudgetBytes
            : OnnxTiler.GetAvailableVramBudget(
                deviceId,
                current.VramUtilizationFraction,
                current.ResolvedVramSafetyMarginBytes,
                current.ResolvedVramExclusiveThresholdBytes
            );
    }

    /// <summary>
    /// Picks a model to calibrate with: a small ESRGAN is preferred because it is fast, it ships in
    /// every model set, and calibrating the cheapest family is the conservative choice.
    /// </summary>
    private static string? SelectCalibrationModel(string modelsDirectory, long budget)
    {
        List<string> candidates = Directory
            .GetFiles(modelsDirectory, "*.onnx")
            .Where(f =>
                !Path.GetFileName(f).Contains("page_break", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(f => new FileInfo(f).Length)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        string? model =
            candidates.FirstOrDefault(f =>
            {
                string name = Path.GetFileName(f);
                return name.Contains("ESRGAN", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("MangaJaNai", StringComparison.OrdinalIgnoreCase);
            }) ?? candidates[0];

        // A calibration model that does not even fit the largest benchmark tile tells us nothing.
        long fileBytes = new FileInfo(model).Length;
        int scale = OnnxUpscaleEngine.InferScaleFromModelName(Path.GetFileName(model));
        OnnxTiler.ActivationCoefficients analytic = OnnxTiler.ActivationCoefficients.For(
            model,
            fileBytes
        );
        int maxTile = (int)
            Math.Sqrt(Math.Max(1.0, budget * 0.25 / analytic.BytesPerPixel(scale, true)));
        return maxTile >= 128 ? model : null;
    }

    /// <summary>
    /// The larger of the two benchmark tile sizes: large enough that the per-pixel term dominates
    /// the measurement noise, small enough (a fraction of the budget) that the benchmark itself
    /// cannot exhaust the device. Pushed as high as that allows, because the linear model is
    /// extrapolated up to the real operating tiles.
    /// </summary>
    private static int ChooseLargeTile(
        int scale,
        bool isFp16,
        OnnxTiler.ActivationCoefficients analytic,
        long budget
    )
    {
        double allowance = Math.Max(64L * 1024 * 1024, budget * 0.3);
        int tile = (int)Math.Sqrt(allowance / analytic.BytesPerPixel(scale, isFp16));
        tile = (tile / 64) * 64;
        return Math.Clamp(tile, 128, 640);
    }

    private static ExecutionProvider ToProvider(GpuBackend backend) =>
        backend switch
        {
            GpuBackend.WebGPU => ExecutionProvider.WebGpu,
            GpuBackend.CUDA => ExecutionProvider.Cuda,
            GpuBackend.DirectML => ExecutionProvider.DirectML,
            GpuBackend.OpenVINO => ExecutionProvider.OpenVino,
            GpuBackend.MIGraphX => ExecutionProvider.MIGraphX,
            GpuBackend.CPU => ExecutionProvider.Cpu,
            _ => ExecutionProvider.Unknown,
        };

    private static string ResolveDeviceName(int deviceId)
    {
        try
        {
            if (
                VulkanMemoryProvider.IsAvailable
                && VulkanMemoryProvider.DeviceCount > deviceId
                && VulkanMemoryProvider.QueryDevice(deviceId) is VulkanGpuMemory vk
                && !string.IsNullOrWhiteSpace(vk.DeviceName)
            )
            {
                return vk.DeviceName;
            }
        }
        catch
        {
            // fall through to a generic name
        }

        return $"device-{deviceId}";
    }

    private static long ResolveTotalDeviceMemory(int deviceId)
    {
        try
        {
            if (
                VulkanMemoryProvider.IsAvailable
                && VulkanMemoryProvider.DeviceCount > deviceId
                && VulkanMemoryProvider.QueryDevice(deviceId) is VulkanGpuMemory vk
                && vk.TotalVramBytes > 0
            )
            {
                return vk.TotalVramBytes;
            }

            var (total, _, _) = OnnxTiler.GetGpuVramInfo(deviceId);
            return total;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Samples device memory on a background thread while an action runs and reports the peak. The
/// ~1 ms sampling interval is the same one the calibration and the engine's own diagnostics use.
/// </summary>
internal static class DeviceMemorySampler
{
    public static long Read(int deviceId)
    {
        var (used, _) = OnnxTiler.GetGpuMemoryUsage(deviceId);
        return used;
    }

    public static long Measure(int deviceId, Action action)
    {
        long before = Read(deviceId);
        using var cts = new CancellationTokenSource();
        long peak = before;
        Task sampler = Task.Run(
            () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    long value = Read(deviceId);
                    if (value > Volatile.Read(ref peak))
                    {
                        Volatile.Write(ref peak, value);
                    }
                    Thread.Sleep(1);
                }
            },
            cts.Token
        );

        try
        {
            action();
            // Peak readings lag the actual allocation, so let the sampler catch up before reading
            Thread.Sleep(20);
        }
        finally
        {
            cts.Cancel();
            try
            {
                sampler.Wait(TimeSpan.FromMilliseconds(100));
            }
            catch
            {
                // best effort
            }
        }

        return Math.Max(Volatile.Read(ref peak), Read(deviceId));
    }
}
