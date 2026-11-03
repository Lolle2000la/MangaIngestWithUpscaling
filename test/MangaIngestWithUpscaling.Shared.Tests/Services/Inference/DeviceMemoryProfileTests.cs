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
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90, DeviceWideMeasured = true };

        Assert.Equal(0.90, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ScaleFor_IgnoresAnOutOfRangeModelMeasurement()
    {
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90, DeviceWideMeasured = true };
        profile.Models[Key] = new ModelMemoryProfile { ActivationScale = 50 };

        Assert.Equal(0.90, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ScaleFor_UnmeasuredDeviceWideDefaults_ReturnNull()
    {
        // The production calibration path writes per-model entries only, so a saved profile carries
        // the device-wide fields at their CLR defaults (1.0 / 0). Reading those as measurements
        // supplied 1.0 as the activation scale and 0 as the session reservation for every model
        // without its own entry — the latter silently replacing the 384 MiB built-in and
        // under-stating the footprint the planner budgets for. Without the flag, "no measurement"
        // must stay distinguishable from "measured as 0".
        var skeleton = new DeviceMemoryProfile();

        Assert.Null(skeleton.ScaleFor(Model, ModelBytes));
        Assert.Null(skeleton.ReservationFor(Model, ModelBytes));
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
    public void ScaleFor_IgnoresAModelMeasurementBelowTheCalibratorsFloor()
    {
        // The calibrator rejects anything below 0.2, so 0.1 is a value it can never have written —
        // corrupt, not a measurement. The reader used to accept "> 0" and hand it on, only for the
        // consumer (ResolveProfileFactors) to discard it again; the two now use the same bound.
        var profile = new DeviceMemoryProfile { ActivationScale = 0.90, DeviceWideMeasured = true };
        profile.Models[Key] = new ModelMemoryProfile { ActivationScale = 0.1 };

        Assert.Equal(0.90, profile.ScaleFor(Model, ModelBytes));
    }

    [Fact]
    public void ReservationFor_AcceptsTheCalibratorsCap()
    {
        // The calibrator only zeroes a reservation *above* 8 GiB, so exactly 8 GiB is a value it can
        // write. The reader and the consumer both used "< 8 GiB" and rejected it, silently swapping
        // the 384 MiB default in; the bound is inclusive to match the producer.
        var profile = new DeviceMemoryProfile();
        profile.Models[Key] = new ModelMemoryProfile
        {
            SessionReservationBytes = 8L * 1024 * 1024 * 1024,
        };

        Assert.Equal(8L * 1024 * 1024 * 1024, profile.ReservationFor(Model, ModelBytes));
    }

    [Fact]
    public void Store_RoundTripsTheDeviceWideMeasuredFlag()
    {
        // The flag is the only thing that separates a measured device-wide 0/1.0 from an untouched
        // skeleton, so it has to survive the JSON round trip as well as the in-memory read.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            store.Save(
                dir,
                new DeviceMemoryProfile
                {
                    Fingerprint = "fp",
                    ActivationScale = 0.90,
                    SessionReservationBytes = 0,
                    DeviceWideMeasured = true,
                }
            );

            var reopened = new DeviceMemoryProfileStore(
                NullLogger<DeviceMemoryProfileStore>.Instance
            );
            DeviceMemoryProfile? loaded = reopened.Load(dir, "fp");

            Assert.NotNull(loaded);
            Assert.True(loaded!.DeviceWideMeasured);
            Assert.Equal(0.90, loaded.ScaleFor(Model, ModelBytes));
            Assert.Equal(0, loaded.ReservationFor(Model, ModelBytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_ProfileWrittenBeforeTheFlag_ReadsAsUnmeasured()
    {
        // A file written before deviceWideMeasured existed has no such field, so it deserialises to
        // false — which is exactly what its device-wide halves are. Without that, the skeleton's
        // default 1.0/0 stood in for a measurement.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                DeviceMemoryProfileStore.PathFor(dir),
                """
                {
                  "fp": {
                    "fingerprint": "fp",
                    "activationScale": 4.5,
                    "sessionReservationBytes": 1048576
                  }
                }
                """
            );

            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            DeviceMemoryProfile? loaded = store.Load(dir, "fp");

            Assert.NotNull(loaded);
            Assert.False(loaded!.DeviceWideMeasured);
            Assert.Null(loaded.ScaleFor(Model, ModelBytes));
            Assert.Null(loaded.ReservationFor(Model, ModelBytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_NullModelEntry_FallsBackToTheBuiltInsInsteadOfThrowing()
    {
        // A JSON "key": null deserialises to a null entry in the dictionary. Read as a measurement it
        // threw a NullReferenceException out of every later resolve; the entry is dropped on load, so
        // the profile resolves to the built-ins ("no measurement") like any other entry-less profile,
        // and the calibrator's ContainsKey cache-hit test does not mistake it for a measurement.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string key = DeviceMemoryProfile.ModelKey(Model, ModelBytes);
            File.WriteAllText(
                DeviceMemoryProfileStore.PathFor(dir),
                $$"""
                {
                  "fp": {
                    "fingerprint": "fp",
                    "deviceWideMeasured": false,
                    "models": {
                      "{{key}}": null
                    }
                  }
                }
                """
            );

            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            DeviceMemoryProfile? loaded = store.Load(dir, "fp");

            Assert.NotNull(loaded);
            Assert.DoesNotContain(key, loaded!.Models.Keys);
            Assert.Empty(loaded.Models);
            Assert.Null(loaded.ScaleFor(Model, ModelBytes));
            Assert.Null(loaded.ReservationFor(Model, ModelBytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
        var profile = new DeviceMemoryProfile
        {
            SessionReservationBytes = 384L * 1024 * 1024,
            DeviceWideMeasured = true,
        };

        Assert.Equal(384L * 1024 * 1024, profile.ReservationFor(Model, ModelBytes));
    }

    [Fact]
    public void ReservationFor_HonorsADeviceWideZeroThatWasMeasured()
    {
        // The flag is the only thing that separates a measured 0 from an untouched skeleton, so a
        // device-wide 0 that a benchmark really produced still has to be honoured rather than
        // falling back to the built-in.
        var profile = new DeviceMemoryProfile
        {
            SessionReservationBytes = 0,
            DeviceWideMeasured = true,
        };

        Assert.Equal(0, profile.ReservationFor(Model, ModelBytes));
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

            // The comparer has to survive the round trip: System.Text.Json materializes a fresh
            // dictionary with the default comparer and assigns it, so the rule the writer chose was
            // lost on load — a key differing only in case then missed its own measurement after a
            // reload while it hit in memory, and two distinct model files could share one entry.
            // Both must agree, and both must be ordinal.
            string differentlyCased = Key.ToUpperInvariant();
            Assert.NotEqual(Key, differentlyCased);
            Assert.Equal(
                profile.Models.ContainsKey(differentlyCased),
                loaded.Models.ContainsKey(differentlyCased)
            );
            Assert.False(loaded.Models.ContainsKey(differentlyCased));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
