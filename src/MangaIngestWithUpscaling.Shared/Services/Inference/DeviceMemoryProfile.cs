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
}
