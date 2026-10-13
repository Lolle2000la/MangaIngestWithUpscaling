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
    [JsonPropertyName("models")]
    public Dictionary<string, ModelMemoryProfile> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The activation scale to plan <paramref name="modelNameOrPath"/> with: its own measurement if
    /// this device has one, otherwise the device-wide default from the small calibration model.
    /// </summary>
    public double ScaleFor(string modelNameOrPath, long modelSizeBytes)
    {
        // The same closed bound the calibrator writes and the tiler's consumer accepts: the
        // calibrator stores exactly 12.0 when a model measures at the cap, and a reader rejecting
        // the boundary silently substituted the device-wide default for that model's own
        // measurement.
        if (
            Models.TryGetValue(ModelKey(modelNameOrPath, modelSizeBytes), out ModelMemoryProfile? m)
            && m.ActivationScale is > 0 and <= 12
        )
        {
            return m.ActivationScale;
        }

        return ActivationScale;
    }

    /// <summary>
    /// The tile-independent session footprint to plan <paramref name="modelNameOrPath"/> with: its own
    /// measurement when this device has one, otherwise the device-wide default. The per-model value
    /// excludes the weights (the planner adds those itself).
    /// </summary>
    public long ReservationFor(string modelNameOrPath, long modelSizeBytes)
    {
        if (
            Models.TryGetValue(ModelKey(modelNameOrPath, modelSizeBytes), out ModelMemoryProfile? m)
            && m.SessionReservationBytes is >= 0 and < 8L * 1024 * 1024 * 1024
        )
        {
            return m.SessionReservationBytes;
        }

        return SessionReservationBytes;
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
