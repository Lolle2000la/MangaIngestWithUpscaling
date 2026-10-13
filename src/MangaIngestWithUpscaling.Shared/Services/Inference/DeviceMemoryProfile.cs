using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// The execution provider a calibration was measured on. The memory behaviour of a model depends
/// far more on this than on the model itself, so it is part of the device identity.
/// </summary>
public enum ExecutionProvider
{
    Unknown,
    Cpu,
    WebGpu,
    Cuda,
    DirectML,
    OpenVino,
    MIGraphX,
}

/// <summary>
/// Measured memory behaviour of one accelerator, used to turn the analytically derived activation
/// working set of <see cref="OnnxTiler"/> into a device-specific estimate.
/// </summary>
public sealed class DeviceMemoryProfile
{
    /// <summary>Stable identity of the device + execution provider this profile belongs to.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public ExecutionProvider Provider { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("totalDeviceMemoryBytes")]
    public long TotalDeviceMemoryBytes { get; set; }

    /// <summary>
    /// Multiplier on the analytically derived activation working set, as measured on this device.
    /// 1.0 means the execution provider costs exactly the theoretical working set; the ORT WebGPU EP
    /// measures ~5.0 because of fp32 casts, layout re-packing and a non-shrinking arena.
    /// </summary>
    [JsonPropertyName("activationScale")]
    public double ActivationScale { get; set; } = 1.0;

    /// <summary>
    /// Device memory held by a live session independently of tile size (execution-provider arenas
    /// and driver-side allocations), in bytes.
    /// </summary>
    [JsonPropertyName("sessionReservationBytes")]
    public long SessionReservationBytes { get; set; }

    /// <summary>
    /// Whether the device-wide factors above were actually measured. Profiles are also saved for the
    /// per-model measurements alone — that is the only production path — and those leave the
    /// device-wide fields at their CLR defaults (0 and 1.0), which are indistinguishable from a
    /// measurement that came out at exactly those values. Without this flag a saved profile supplied
    /// 0 as "the device-wide session reservation" for every model without an entry of its own,
    /// silently replacing the built-in and under-stating the footprint the planner budgets for.
    /// Profiles written before the flag read as unmeasured, which is what their device-wide halves
    /// are.
    /// </summary>
    [JsonPropertyName("deviceWideMeasured")]
    public bool DeviceWideMeasured { get; set; }

    [JsonPropertyName("calibratedAtUtc")]
    public DateTimeOffset CalibratedAtUtc { get; set; }

    [JsonPropertyName("calibratedWithModel")]
    public string CalibratedWithModel { get; set; } = string.Empty;

    [JsonPropertyName("measuredTiles")]
    public int[] MeasuredTiles { get; set; } = [];

    [JsonPropertyName("measuredPeakBytes")]
    public long[] MeasuredPeakBytes { get; set; } = [];

    /// <summary>
    /// Per-model measurements on this device, keyed by <see cref="ModelKey"/>. The per-pixel cost is
    /// a property of the model and the execution provider together, not of the device: measured on
    /// an RX 9070 XT / WebGPU EP the fp16 ESRGAN needs 0.90x its analytic working set, the fp32
    /// ESRGAN 0.69x and the fp32 FDAT_M 1.28x. A single device-wide factor therefore under-estimates
    /// the fp32 transformer by ~25% and over-estimates the conv nets, which is what made a chapter
    /// spill into GTT. Entries are added lazily, one per model actually used.
    /// </summary>
    private Dictionary<string, ModelMemoryProfile> _models = new(StringComparer.Ordinal);

    [JsonPropertyName("models")]
    public Dictionary<string, ModelMemoryProfile> Models
    {
        get => _models;
        // System.Text.Json materializes a fresh dictionary with the default (ordinal) comparer and
        // assigns it on read, so the comparer the writer chose was lost across a save/load — a key
        // differing in case then missed its own measurement, and (on a case-sensitive filesystem)
        // two distinct model files could share one. Re-wrap on the way in so the lookup rule is the
        // same before and after a round trip. Ordinal rather than OrdinalIgnoreCase: the key is
        // generated from the same path string it is looked up with, and the engine's model maps are
        // ordinal so that distinct files stay distinct.
        //
        // A JSON null value ("model": null, e.g. a hand-edited or truncated file) deserialises to a
        // null entry at runtime. Dropped here rather than only guarded at the read sites below: an
        // entry that is *present* but null otherwise reads to the calibrator as "already measured"
        // (its cache-hit test is ContainsKey), so the model would never be benchmarked and would
        // silently keep the built-in coefficients forever.
        set =>
            _models = value is null
                ? new Dictionary<string, ModelMemoryProfile>(StringComparer.Ordinal)
                : value
                    .Where(entry => entry.Value is not null)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The closed range the calibrator writes and the planner accepts for
    /// <see cref="ActivationScale"/>. The calibrator refuses a measurement outside it
    /// (<c>DeviceMemoryCalibrator.RunBenchmark</c>), so a value outside this range is a corrupt profile
    /// rather than a measurement, and reading it as one would replace the built-in with nonsense. Both
    /// ends are inclusive because the calibrator's own check is <c>&lt; 0.2 or &gt; 12.0</c>: exactly
    /// 12.0 is a real measurement taken at the cap, and rejecting it discarded that model's own
    /// measurement.
    /// </summary>
    internal const double MinActivationScale = 0.2;

    internal const double MaxActivationScale = 12.0;

    /// <summary>
    /// Upper bound, inclusive, on <see cref="SessionReservationBytes"/> and its per-model counterpart,
    /// shared by the calibrator, this resolver and the planner. 0 is a valid measurement, not "no
    /// measurement" — the calibrator writes exactly 0 when it measures no tile-independent footprint —
    /// so the bound is closed at both ends and the distinction from "unmeasured" is
    /// <see cref="DeviceWideMeasured"/>, never the value.
    /// </summary>
    internal const long MaxSessionReservationBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// The activation scale to plan <paramref name="modelNameOrPath"/> with: its own measurement if
    /// this device has one, the device-wide default if that was measured, otherwise null — "no
    /// measurement", so the caller keeps its built-in rather than a skeleton's 1.0.
    /// </summary>
    public double? ScaleFor(string modelNameOrPath, long modelSizeBytes)
    {
        // The same closed bound the calibrator writes and the tiler's consumer accepts: the
        // calibrator stores exactly 12.0 when a model measures at the cap, and a reader rejecting
        // the boundary silently substituted the device-wide default for that model's own
        // measurement. The lower bound is the writer's (0.2), not "> 0": a value between 0 and 0.2 is
        // one the calibrator would have refused, so it is corruption, and the consumer would discard
        // it anyway — returning it only to be filtered is the same as not having the bound at all.
        if (
            Models.TryGetValue(ModelKey(modelNameOrPath, modelSizeBytes), out ModelMemoryProfile? m)
            && m is not null
            && m.ActivationScale is >= MinActivationScale and <= MaxActivationScale
        )
        {
            return m.ActivationScale;
        }

        // The device-wide half is held to the same range: it is written by the same calibrator and its
        // value is only ever used when DeviceWideMeasured says the producer really filled it in.
        return
            DeviceWideMeasured && ActivationScale is >= MinActivationScale and <= MaxActivationScale
            ? ActivationScale
            : null;
    }

    /// <summary>
    /// The tile-independent session footprint to plan <paramref name="modelNameOrPath"/> with: its own
    /// measurement when this device has one, the device-wide default if that was measured, otherwise
    /// null — "no measurement", so the caller keeps its built-in. The per-model value excludes the
    /// weights (the planner adds those itself).
    /// </summary>
    public long? ReservationFor(string modelNameOrPath, long modelSizeBytes)
    {
        if (
            Models.TryGetValue(ModelKey(modelNameOrPath, modelSizeBytes), out ModelMemoryProfile? m)
            && m is not null
            && m.SessionReservationBytes is >= 0 and <= MaxSessionReservationBytes
        )
        {
            return m.SessionReservationBytes;
        }

        return
            DeviceWideMeasured && SessionReservationBytes is >= 0 and <= MaxSessionReservationBytes
            ? SessionReservationBytes
            : null;
    }

    /// <summary>
    /// Identity of a model within a device profile. The file name alone is not enough: the fp16 and
    /// fp32 exports of the same model differ only in size and precision, and a re-exported file can
    /// keep its name while its graph changes, so the size is part of the key.
    /// </summary>
    public static string ModelKey(string modelNameOrPath, long modelSizeBytes) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Path.GetFileName(modelNameOrPath)}|{modelSizeBytes}"
        );
}

/// <summary>
/// One model's measured memory behaviour on one device. Mirrors the device-level fields that used to
/// be the only measurement there was.
/// </summary>
public sealed class ModelMemoryProfile
{
    [JsonPropertyName("activationScale")]
    public double ActivationScale { get; set; } = 1.0;

    /// <summary>
    /// Tile-independent session footprint for this model on this device, excluding the model
    /// weights (the planner adds those separately), in bytes.
    /// </summary>
    [JsonPropertyName("sessionReservationBytes")]
    public long SessionReservationBytes { get; set; }

    [JsonPropertyName("measuredTiles")]
    public int[] MeasuredTiles { get; set; } = [];

    /// <summary>
    /// Device-memory growth over the pre-session baseline at each of <see cref="MeasuredTiles"/>,
    /// including the model weights and the session's own arena.
    /// </summary>
    [JsonPropertyName("measuredPeakBytes")]
    public long[] MeasuredPeakBytes { get; set; } = [];

    [JsonPropertyName("calibratedAtUtc")]
    public DateTimeOffset CalibratedAtUtc { get; set; }
}
