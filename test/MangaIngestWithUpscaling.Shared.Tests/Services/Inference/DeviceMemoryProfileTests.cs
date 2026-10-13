using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// The profile resolves its two factors per model. The per-pixel cost belongs to the model and the
/// execution provider together: measured on an RX 9070 XT / WebGPU EP the fp16 ESRGAN needs 0.90x its
/// analytic working set, the fp32 ESRGAN 0.69x and the fp32 FDAT_M 1.28x, so one device-wide factor
/// cannot stand in for all of them.
/// </summary>
public class DeviceMemoryProfileTests
{
    private const string Model = "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx";
    private const long ModelBytes = 16_761_523;

    private static string Key => DeviceMemoryProfile.ModelKey(Model, ModelBytes);

    [Fact]
    public void ScaleFor_PrefersTheModelsOwnMeasurement()
    {
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90 };
        profile.Models[Key] = new ModelMemoryProfile { ActivationScale = 1.28 };

        Assert.Equal(1.28, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ScaleFor_FallsBackToTheDeviceDefault()
    {
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90 };

        Assert.Equal(0.90, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ScaleFor_IgnoresAnOutOfRangeModelMeasurement()
    {
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90 };
        profile.Models[Key] = new ModelMemoryProfile { ActivationScale = 50 };

        Assert.Equal(0.90, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ScaleFor_AcceptsTheBoundaryTheCalibratorWrites()
    {
        // The calibrator rejects only above 12.0, so exactly 12.0 is written when a model
        // measures at the cap. A reader rejecting the boundary silently substituted the
        // device-wide default for that model's own measurement — the reader's bound now
        // matches the writer's and the tiler consumer's.
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90 };
        profile.Models[Key] = new ModelMemoryProfile { ActivationScale = 12.0 };

        Assert.Equal(12.0, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ResolveProfileFactors_HonorsACalibratedZeroReservation()
    {
        // A calibration that measured no tile-independent footprint is a legitimate answer (the
        // model's session lives inside the activation estimate), and the calibrator writes
        // exactly 0 for one. The consumer used to demand > 0 and silently swapped the built-in
        // reservation in, oversizing every tile of that model for the rest of the profile's
        // life — the same class of silent substitution as the rejected 12.0 above.
        var profile = new DeviceMemoryProfile();
        profile.Models[Key] = new ModelMemoryProfile { SessionReservationBytes = 0 };

        (_, long reservation) = OnnxTiler.ResolveProfileFactors(profile, Model, ModelBytes);

        Assert.Equal(0, reservation);
    }

    [Fact]
    public void ReservationFor_PrefersTheModelsOwnMeasurement()
    {
        var profile = new DeviceMemoryProfile { SessionReservationBytes = 384L * 1024 * 1024 };
        profile.Models[Key] = new ModelMemoryProfile
        {
            SessionReservationBytes = 12L * 1024 * 1024,
        };

        Assert.Equal(12L * 1024 * 1024, profile.ReservationFor(Model, ModelBytes));
    }

    [Fact]
    public void ReservationFor_FallsBackToTheDeviceDefault()
    {
        var profile = new DeviceMemoryProfile { SessionReservationBytes = 384L * 1024 * 1024 };

        Assert.Equal(384L * 1024 * 1024, profile.ReservationFor(Model, ModelBytes));
    }

    [Fact]
    public void ModelKey_DistinguishesTheSameNameAtADifferentSize()
    {
        // A re-exported model can keep its file name while its graph changes; the size is what
        // catches that, so a measurement taken on the old file is not applied to the new one.
        Assert.NotEqual(
            DeviceMemoryProfile.ModelKey(Model, 100),
            DeviceMemoryProfile.ModelKey(Model, 200)
        );
    }

    [Fact]
    public void Store_RoundTripsPerModelMeasurements()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var profile = new DeviceMemoryProfile
            {
                Fingerprint = "webgpu|0|gpu|17095983104|1.29.0|v3",
                ActivationScale = 0.90,
                SessionReservationBytes = 126_316_544,
            };
            profile.Models[Key] = new ModelMemoryProfile
            {
                ActivationScale = 1.28,
                SessionReservationBytes = 9_437_184,
                MeasuredTiles = [320, 640],
                MeasuredPeakBytes = [2_100_000_000, 13_100_000_000],
                CalibratedAtUtc = DateTimeOffset.UnixEpoch,
            };
            store.Save(dir, profile);

            var reopened = new DeviceMemoryProfileStore(
                NullLogger<DeviceMemoryProfileStore>.Instance
            );
            DeviceMemoryProfile? loaded = reopened.Load(dir, profile.Fingerprint);

            Assert.NotNull(loaded);
            Assert.Equal(1.28, loaded!.ScaleFor(Model, ModelBytes));
            Assert.Equal(9_437_184, loaded.ReservationFor(Model, ModelBytes));
            Assert.Equal([320, 640], loaded.Models[Key].MeasuredTiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
