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
    /// Returns the calibration for the current device, running the benchmark once if no valid
    /// profile exists yet. Never throws: a failed benchmark falls back to the built-in coefficients.
    /// </summary>
    Task<DeviceMemoryProfile?> GetOrCalibrateAsync(
        int deviceId,
        string modelsDirectory,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Runs a short inference benchmark on the GPU to derive the two numbers that cannot be read off
/// the model graph:
/// <list type="bullet">
/// <item><see cref="DeviceMemoryProfile.ActivationScale"/> — how many times the analytically
/// derived activation working set the execution provider really needs (precision casts, layout
/// re-packing, non-shrinking arenas)</item>
/// <item><see cref="DeviceMemoryProfile.SessionReservationBytes"/> — how much device memory a live
/// session holds independently of tile size</item>
/// </list>
/// Two tile sizes give two measurements, and their difference isolates the per-pixel term:
/// <c>peak(n) = reservation + scale · analytic · n²</c>.
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
    /// stored by an older build is re-measured instead of trusted.
    /// </summary>
    public const int CalibrationVersion = 2;

    private static readonly Lock BenchmarkGate = new();
    private readonly SemaphoreSlim asyncGate = new(1, 1);

    /// <summary>
    /// Fingerprints the benchmark has already been attempted for and failed on, so it is not
    /// retried for every page. Without this, a models directory the profile cannot be written to
    /// (a read-only volume, a container running as an arbitrary uid) makes <see cref="GetOrCalibrateAsync"/>
    /// return null forever — and every page pays three inferences plus a session teardown to reach it.
    /// Scoped to the process: a worker that skips a chapter because of it re-benchmarks on the next.
    /// </summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);

    public async Task<DeviceMemoryProfile?> GetOrCalibrateAsync(
        int deviceId,
        string modelsDirectory,
        CancellationToken cancellationToken = default
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
            if (!config.Value.RecalibrateDeviceMemory)
            {
                DeviceMemoryProfile? cached = store.Load(modelsDirectory, fingerprint);
                if (cached is not null)
                {
                    return cached;
                }
            }

            // CPU mode has no per-tile footprint worth calibrating: the activation working set is
            // allocated in system RAM, not on a device with a budget we are trying to fill.
            if (config.Value.UseCPU)
            {
                return null;
            }

            DeviceMemoryProfile? profile;
            try
            {
                profile = RunBenchmark(deviceId, modelsDirectory, fingerprint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!config.Value.RecalibrateDeviceMemory)
                {
                    RecordUnusable(fingerprint);
                }

                logger.LogWarning(
                    ex,
                    "Device memory benchmark failed; using built-in coefficients"
                );
                return null;
            }

            if (profile is null && !config.Value.RecalibrateDeviceMemory)
            {
                RecordUnusable(fingerprint);
            }

            // A measured profile is deliberately not recorded here. Recording it would make the
            // next page take the "already tried" short-cut and get null on a models directory the
            // profile could not be written back to, throwing away a measurement that worked.
            return profile;
        }
        finally
        {
            asyncGate.Release();
        }
    }

    /// <summary>
    /// Remembers that the benchmark produced nothing usable for this fingerprint, so it is not run
    /// again for every page.
    /// </summary>
    private void RecordUnusable(string fingerprint) => _unsupported.Add(fingerprint);

    /// <summary>
    /// Identity of the measured configuration. The execution provider comes from the session factory
    /// (the single place that resolves it, honouring <see cref="UpscalerConfig.PreferredGpuBackend"/>,
    /// <see cref="UpscalerConfig.UseCPU"/> and the device index), so a calibration is only ever
    /// reused for the provider that will actually run the model.
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

    private DeviceMemoryProfile? RunBenchmark(
        int deviceId,
        string modelsDirectory,
        string fingerprint
    )
    {
        string? modelPath = SelectCalibrationModel(modelsDirectory, deviceId);
        if (modelPath is null)
        {
            logger.LogDebug(
                "No model small enough to benchmark device memory with; using built-in coefficients"
            );
            return null;
        }

        // Never run the benchmark concurrently with a real upscale: both would need the device.
        lock (BenchmarkGate)
        {
            long fileBytes = new FileInfo(modelPath).Length;
            int scale = OnnxUpscaleEngine.InferScaleFromModelName(Path.GetFileName(modelPath));

            // Drop a cached session for this model so the measurement pays — and therefore sees —
            // the real cost of bringing a session up on this device.
            sessionFactory.InvalidateSession(modelPath);

            long baseline = DeviceMemorySampler.Read(deviceId);
            var session = sessionFactory.GetOrCreateSession(modelPath);

            try
            {
                bool isFp16 =
                    session.InputMetadata.TryGetValue("input", out var meta)
                    && meta.ElementType == typeof(Float16);
                OnnxTiler.ActivationCoefficients analytic = OnnxTiler.ActivationCoefficients.For(
                    modelPath,
                    fileBytes
                );

                long budget = OnnxTiler.GetAvailableVramBudget(deviceId);
                int large = ChooseLargeTile(scale, isFp16, analytic, budget);
                int small = Math.Max(96, (large / 3 / 64) * 64);
                const int warmup = 64;
                if (small >= large)
                {
                    return null;
                }

                // The execution provider grows its arenas lazily on the first inference, so the
                // warm-up pass pays that cost. The tiny tile's peak then measures everything that
                // is fixed (session state, arenas, driver allocations), which leaves the two
                // measured tiles to give a clean per-pixel slope.
                long floor = MeasureTile(session, deviceId, warmup, scale);
                long peakSmall = MeasureTile(session, deviceId, small, scale);
                long peakLarge = MeasureTile(session, deviceId, large, scale);

                double analyticBytesPerPixel = analytic.BytesPerPixel(scale, isFp16);
                double bytesPerPixel =
                    (peakLarge - peakSmall) / (double)((long)large * large - (long)small * small);
                double activationScale =
                    analyticBytesPerPixel > 0 ? bytesPerPixel / analyticBytesPerPixel : 1.0;
                long reservation = Math.Max(0, floor - baseline);

                // Sanity: a factor far outside the plausible range means the measurement was
                // distorted (another process on the device, driver eviction, ...). Keep built-ins.
                if (
                    activationScale is < 0.2 or > 12.0
                    || reservation < 0
                    || reservation > 8L * 1024 * 1024 * 1024
                )
                {
                    logger.LogWarning(
                        "Device memory benchmark produced an implausible result (activationScale={Scale:F2}, sessionReservation={Reservation} MB); keeping built-in coefficients",
                        activationScale,
                        reservation / 1048576
                    );
                    return null;
                }

                var profile = new DeviceMemoryProfile
                {
                    Fingerprint = fingerprint,
                    Provider = ToProvider(sessionFactory.GetEffectiveBackend()),
                    DeviceName = ResolveDeviceName(deviceId),
                    TotalDeviceMemoryBytes = ResolveTotalDeviceMemory(deviceId),
                    ActivationScale = activationScale,
                    SessionReservationBytes = reservation,
                    CalibratedAtUtc = DateTimeOffset.UtcNow,
                    CalibratedWithModel = Path.GetFileName(modelPath),
                    MeasuredTiles = [small, large],
                    MeasuredPeakBytes = [peakSmall - floor, peakLarge - floor],
                };

                store.Save(modelsDirectory, profile);
                logger.LogInformation(
                    "Calibrated device memory profile for {Device} on {Provider}: activation scale {Scale:F2}x, session reservation {Reservation} MB (measured at {Small}px and {Large}px tiles with {Model})",
                    profile.DeviceName,
                    profile.Provider,
                    activationScale,
                    reservation / 1048576,
                    small,
                    large,
                    profile.CalibratedWithModel
                );
                return profile;
            }
            finally
            {
                sessionFactory.InvalidateSession(modelPath);
            }
        }
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

    /// <summary>
    /// Picks a model to calibrate with: a small ESRGAN is preferred because it is fast, it ships in
    /// every model set, and calibrating the cheapest family is the conservative choice.
    /// </summary>
    private static string? SelectCalibrationModel(string modelsDirectory, int deviceId)
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
        long budget = OnnxTiler.GetAvailableVramBudget(deviceId);
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
