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
    public void TwoStoresOverTheSameFile_ShareEntriesWithoutDroppingEachOther()
    {
        // Two workers share the models volume. Each instance caches the file it last read, so a
        // write by one must not be served from the other's stale snapshot, and a Save must merge
        // into what is actually on disk rather than clobbering the other worker's profile.
        var workerA = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        var workerB = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);

        // A reads the (empty) file first and caches it, so A's snapshot is stale after B writes.
        Assert.Null(workerA.Load(tempDir, "a"));

        workerA.Save(tempDir, NewProfile("a", "A"));
        Assert.NotNull(workerB.Load(tempDir, "a"));

        workerB.Save(tempDir, NewProfile("b", "B"));
        Assert.NotNull(workerA.Load(tempDir, "b"));

        // A now saves an update to its own entry. Its cached snapshot predates B's write, so a
        // read-modify-write from the cache would drop B's profile.
        var updatedA = NewProfile("a", "A");
        updatedA.ActivationScale = 12.5;
        workerA.Save(tempDir, updatedA);

        var fresh = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        Assert.Equal(12.5, fresh.Load(tempDir, "a")!.ActivationScale);
        Assert.NotNull(fresh.Load(tempDir, "b"));

        // B re-reads the changed file instead of serving its snapshot.
        Assert.Equal(12.5, workerB.Load(tempDir, "a")!.ActivationScale);
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

    [Fact]
    public void TempPath_IsUniquePerWriter()
    {
        // The store file is explicitly shared between hosts, so a fixed "<path>.tmp" name lets two
        // writers interleave the same scratch file and the atomic move then publishes a torn mixture
        // of both. Every save has to use its own.
        string path = Path.Combine(tempDir, "device-memory-profile.json");

        string first = DeviceMemoryProfileStore.TempPath(path);
        string second = DeviceMemoryProfileStore.TempPath(path);

        Assert.NotEqual(first, second);
        Assert.NotEqual(path + ".tmp", first);
        Assert.StartsWith(path, first, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_LeavesNoScratchFileBehind()
    {
        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);

        store.Save(tempDir, NewProfile("fp1", "gpu"));

        Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        Assert.True(File.Exists(DeviceMemoryProfileStore.PathFor(tempDir)));
    }

    [Fact]
    public void DeleteStaleTemps_ReclaimsOnlyOldScratch()
    {
        // Save deletes its uniquely-named write scratch on every path it can reach, but a hard kill
        // between creating the temp and moving it, or a move and a delete that both fail, leaves it
        // in the models directory — and the model-download sweep matches only its own prefixes. The
        // sweep reclaims only old scratch, never a live save's or another mechanism's temp file.
        string stale = DeviceMemoryProfileStore.TempPath(DeviceMemoryProfileStore.PathFor(tempDir));
        string fresh = DeviceMemoryProfileStore.TempPath(DeviceMemoryProfileStore.PathFor(tempDir));
        string unrelated = Path.Combine(tempDir, "model_pkg_abcd.zip.tmp");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.WriteAllText(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(2));

        DeviceMemoryProfileStore.DeleteStaleTemps(
            tempDir,
            NullLogger<DeviceMemoryProfileStore>.Instance,
            TimeSpan.FromHours(24)
        );

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Save_WhenTheProfileCannotBePersisted_ServesTheMeasurementFromMemory()
    {
        // A regression test for the property, not a fix for a bug: Save reads the file into its own
        // dictionary (TryReadFile), layers the measurement onto that same instance in place, and
        // publishes it to the cache before it attempts the write — so the measurement is cached
        // whether or not the file could be written. It is pinned anyway because the failure mode is
        // silent and expensive — a cache that missed it would make the caller re-run the multi-second
        // benchmark for every page — and because it would break the moment Save published the cache
        // only after a successful write.
        // A directory standing in for the profile file makes the move fail without depending on
        // filesystem permissions, which a root test run would bypass.
        Directory.CreateDirectory(DeviceMemoryProfileStore.PathFor(tempDir));

        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        store.Save(tempDir, NewProfile("fp-unwritable", "gpu"));

        DeviceMemoryProfile? loaded = store.Load(tempDir, "fp-unwritable");
        Assert.NotNull(loaded);
        Assert.Equal(5.0, loaded!.ActivationScale);
    }

    [Fact]
    public void Save_MergesTheFreshlyReadEntrysPerModelMeasurements()
    {
        // The calibrator measures one model at a time on a profile it loaded earlier and saves that
        // whole object back. A per-model entry another process added under the same fingerprint in
        // the meantime has to survive the save: replacing the device entry wholesale silently drops
        // it, and the other process's measurement is lost for every later run too.
        string keyA = DeviceMemoryProfile.ModelKey("4x_ESRGAN_fp16.onnx", 100);
        string keyB = DeviceMemoryProfile.ModelKey("4x_FDAT_M_fp32.onnx", 200);
        string keyC = DeviceMemoryProfile.ModelKey("4x_ESRGAN_fp32.onnx", 300);

        var workerA = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        var snapshotA = NewProfile("shared-fp", "gpu");
        snapshotA.Models[keyA] = new ModelMemoryProfile { ActivationScale = 0.90 };
        workerA.Save(tempDir, snapshotA);

        // Worker B loads the file A wrote and adds its own model under the same fingerprint.
        var workerB = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        DeviceMemoryProfile fromB = workerB.Load(tempDir, "shared-fp")!;
        fromB.Models[keyB] = new ModelMemoryProfile { ActivationScale = 1.28 };
        workerB.Save(tempDir, fromB);

        // Worker A now saves the snapshot it loaded before B's write, with the model it just
        // measured layered on. A replace would drop B's entry.
        snapshotA.Models[keyC] = new ModelMemoryProfile { ActivationScale = 0.69 };
        workerA.Save(tempDir, snapshotA);

        var fresh = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        DeviceMemoryProfile? merged = fresh.Load(tempDir, "shared-fp");

        Assert.NotNull(merged);
        Assert.True(merged!.Models.ContainsKey(keyA));
        Assert.True(merged.Models.ContainsKey(keyB)); // dropped without the merge
        Assert.True(merged.Models.ContainsKey(keyC));
        Assert.Equal(1.28, merged.Models[keyB].ActivationScale);
    }

    [Fact]
    public void Save_AfterAReadError_DoesNotClobberTheFileWithAnEmptySnapshot()
    {
        // A read that fails while the file exists (a transient sharing violation, a network volume,
        // a scanner) reports "unknown", not "no profiles". Publishing that as an empty snapshot let
        // the next Save write it over the file and destroy every profile in it. The file is held
        // open exclusively here to force exactly that read failure; its original content has to
        // survive the save.
        var writer = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        writer.Save(tempDir, NewProfile("shared", "gpu"));
        var other = NewProfile("other", "gpu");
        other.ActivationScale = 2.5;
        writer.Save(tempDir, other);

        string path = DeviceMemoryProfileStore.PathFor(tempDir);

        var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // FileShare.None is emulated with an advisory lock on Unix and can be disabled (the
            // System.IO.DisableFileLocking switch); the test only means something while enforced.
            bool lockingEnforced = false;
            try
            {
                using FileStream probe = File.OpenRead(path);
            }
            catch (IOException)
            {
                lockingEnforced = true;
            }
            Assert.SkipWhen(!lockingEnforced, "FileShare.None is not enforced on this host");

            // The read error is not published as an empty "no profiles" snapshot...
            Assert.Null(store.Load(tempDir, "shared"));

            // ...so the save must refuse to overwrite the unreadable file with an empty dictionary
            // instead of destroying the profiles it could not read.
            var measured = NewProfile("shared", "gpu");
            measured.ActivationScale = 7.0;
            store.Save(tempDir, measured);
        }

        var fresh = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        DeviceMemoryProfile? shared = fresh.Load(tempDir, "shared");
        Assert.NotNull(shared);
        Assert.Equal(5.0, shared!.ActivationScale);
        Assert.NotNull(fresh.Load(tempDir, "other"));
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
