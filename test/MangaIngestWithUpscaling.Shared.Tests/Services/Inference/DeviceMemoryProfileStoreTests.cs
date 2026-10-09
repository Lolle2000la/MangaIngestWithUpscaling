using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

public class DeviceMemoryProfileStoreTests : IDisposable
{
    private readonly string tempDir;

    public DeviceMemoryProfileStoreTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        Assert.Null(store.Load(tempDir, "fingerprint"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsProfile()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        var profile = new DeviceMemoryProfile
        {
            Fingerprint = "webgpu|Test GPU|1234|1.0.0|v1",
            Provider = ExecutionProvider.WebGpu,
            DeviceName = "Test GPU",
            TotalDeviceMemoryBytes = 16L * 1024 * 1024 * 1024,
            ActivationScale = 5.21,
            SessionReservationBytes = 384L * 1024 * 1024,
            CalibratedAtUtc = DateTimeOffset.UnixEpoch,
            CalibratedWithModel = "4x_Model.onnx",
            MeasuredTiles = [192, 448],
            MeasuredPeakBytes = [1_000_000, 4_000_000],
        };

        store.Save(tempDir, profile);

        // A fresh store must read the same file back from disk (no in-process cache reuse)
        var second = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        DeviceMemoryProfile? loaded = second.Load(tempDir, profile.Fingerprint);

        Assert.NotNull(loaded);
        Assert.Equal(ExecutionProvider.WebGpu, loaded!.Provider);
        Assert.Equal("Test GPU", loaded.DeviceName);
        Assert.Equal(5.21, loaded.ActivationScale);
        Assert.Equal(384L * 1024 * 1024, loaded.SessionReservationBytes);
        Assert.Equal([192, 448], loaded.MeasuredTiles);
        Assert.Equal([1_000_000, 4_000_000], loaded.MeasuredPeakBytes);
    }

    [Fact]
    public void Save_KeepsMultipleDeviceProfilesInOneFile()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        var amd = NewProfile("webgpu|AMD Radeon RX 9070 XT|1|ort|v1", "AMD Radeon RX 9070 XT");
        var nvidia = NewProfile("cuda|NVIDIA RTX 4090|2|ort|v1", "NVIDIA RTX 4090");
        var intel = NewProfile("webgpu|Intel Arc|3|ort|v1", "Intel Arc");

        store.Save(tempDir, amd);
        store.Save(tempDir, nvidia);
        store.Save(tempDir, intel);

        var second = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        Assert.NotNull(second.Load(tempDir, amd.Fingerprint));
        Assert.NotNull(second.Load(tempDir, nvidia.Fingerprint));
        Assert.NotNull(second.Load(tempDir, intel.Fingerprint));
        Assert.Null(second.Load(tempDir, "cuda|NVIDIA RTX 5090|4|ort|v1"));
    }

    [Fact]
    public void Save_OverwritesProfileForSameFingerprintOnly()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        var first = NewProfile("a", "A");
        first.ActivationScale = 3.0;
        var other = NewProfile("b", "B");
        var updated = NewProfile("a", "A");
        updated.ActivationScale = 7.0;

        store.Save(tempDir, first);
        store.Save(tempDir, other);
        store.Save(tempDir, updated);

        var second = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        Assert.Equal(7.0, second.Load(tempDir, "a")!.ActivationScale);
        Assert.NotNull(second.Load(tempDir, "b"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullAndCanBeRewritten()
    {
        File.WriteAllText(DeviceMemoryProfileStore.PathFor(tempDir), "{ this is not json");
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);

        Assert.Null(store.Load(tempDir, "a"));

        store.Save(tempDir, NewProfile("a", "A"));
        var second = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        Assert.NotNull(second.Load(tempDir, "a"));
    }

    [Fact]
    public void Save_WritesValidJsonDocument()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        store.Save(tempDir, NewProfile("a", "A"));

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(DeviceMemoryProfileStore.PathFor(tempDir))
        );
        Assert.True(document.RootElement.TryGetProperty("a", out JsonElement _));
    }

    private static DeviceMemoryProfile NewProfile(string fingerprint, string deviceName) =>
        new()
        {
            Fingerprint = fingerprint,
            Provider = ExecutionProvider.WebGpu,
            DeviceName = deviceName,
            ActivationScale = 5.0,
            SessionReservationBytes = 384L * 1024 * 1024,
            CalibratedAtUtc = DateTimeOffset.UnixEpoch,
        };
}
