using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// The per-shape footprints the engine measures while it runs a page.
/// <para>
/// The per-model calibration fits its slope at two small benchmark tiles (96 and 192 px) and
/// extrapolates it to the operating tile, so a provider whose overhead does not scale exactly
/// quadratically is estimated rather than measured. The engine measures the real figure on every page
/// it builds a fresh shape for, and those measurements are what these tests keep: they must survive a
/// re-measurement, stay keyed by the shape they were taken at, and never be invented.
/// </para>
/// </summary>
public class ObservedTilePeakTests : IDisposable
{
    private const int Scale = 4;
    private const int PaddedWidth = 640;
    private const int PaddedHeight = 864;

    private readonly string _tempDir;
    private readonly DeviceMemoryProfileStore _store;
    private readonly DeviceMemoryCalibrator _calibrator;

    public ObservedTilePeakTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"observed_peaks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(new UpscalerConfig { ModelsDirectory = _tempDir });
        _store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
        _calibrator = new DeviceMemoryCalibrator(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance),
            config,
            _store,
            NullLogger<DeviceMemoryCalibrator>.Instance
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void RecordedPeak_IsReadBackForTheShapeAndScaleItWasMeasuredAt()
    {
        (string model, long size) = SeedModelWithMeasurement();

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        DeviceMemoryProfile reloaded = ReloadFreshStore();
        Assert.Equal(
            9604L,
            reloaded.ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );
        // A shape this device has not run, and the same shape at another scale, have no measurement —
        // the footprint is a property of the tensors, and the scale is half of what they are.
        Assert.Null(reloaded.ObservedTilePeakFor(model, size, Scale, 512, 512));
        Assert.Null(reloaded.ObservedTilePeakFor(model, size, 2, PaddedWidth, PaddedHeight));
        Assert.Null(
            reloaded.ObservedTilePeakFor(
                Path.Combine(_tempDir, "another.onnx"),
                size,
                Scale,
                PaddedWidth,
                PaddedHeight
            )
        );
    }

    [Fact]
    public void Recording_NeverInventsAModelEntry()
    {
        // The entry is the calibration's, and a skeleton of one is actively harmful: ScaleFor returns
        // any value inside the accepted range, and a skeleton's ActivationScale of 1.0 is one, so it
        // would take the place of the built-in coefficient for a model the calibrator could not
        // measure. A model with no entry keeps the analytic estimate.
        string model = Path.Combine(_tempDir, "never-calibrated.onnx");
        File.WriteAllBytes(model, new byte[1024]);
        long size = new FileInfo(model).Length;

        var profile = new DeviceMemoryProfile
        {
            Fingerprint = _calibrator.BuildFingerprint(0),
            ActivationScale = 0.9,
            // Deliberately not DeviceWideMeasured: with no device-wide fallback either, a skeleton
            // entry for this model would be the only value ScaleFor could return — and it would be
            // 1.0, taking the place of the built-in coefficient.
            DeviceWideMeasured = false,
        };
        profile.Models[DeviceMemoryProfile.ModelKey("measured-other.onnx", size)] =
            new ModelMemoryProfile { ActivationScale = 0.9486 };
        _store.Save(_tempDir, profile);

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        DeviceMemoryProfile reloaded = ReloadFreshStore();
        Assert.False(reloaded.Models.ContainsKey(DeviceMemoryProfile.ModelKey(model, size)));
        Assert.Null(reloaded.ScaleFor(model, size));
        Assert.Null(reloaded.ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight));
    }

    [Fact]
    public void Recording_WithNoProfileAtAll_WritesNothing()
    {
        (string model, _) = SeedModel(Path.Combine(_tempDir, "uncalibrated-device.onnx"));

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        Assert.Null(ReloadFreshStoreOrNull());
    }

    [Fact]
    public void Peak_IsKeptAtItsLargest()
    {
        // A later process re-measures the same shape from a baseline taken after its session already
        // existed, so it comes out a few tens of megabytes smaller. A plan that moved between chapters
        // for that would be churn for nothing — and the larger figure is the conservative one.
        (string model, long size) = SeedModelWithMeasurement();

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );
        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9550L
        );
        Assert.Equal(
            9604L,
            ReloadFreshStore().ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            10000L
        );
        Assert.Equal(
            10000L,
            ReloadFreshStore().ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );
    }

    [Fact]
    public void Recording_IntoAnUnwritableDirectory_IsSwallowed()
    {
        // The measurement is an optimisation for the next plan. A models directory that cannot be
        // written (a read-only volume, a container running as an arbitrary uid) must not fail a page.
        const string readOnly = "/proc/definitely-not-writable";

        _calibrator.RecordObservedTilePeak(
            0,
            readOnly,
            "a.onnx",
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );
    }

    [Fact]
    public void Recording_OfANonPositiveFigure_IsIgnored()
    {
        (string model, long size) = SeedModelWithMeasurement();

        _calibrator.RecordObservedTilePeak(0, _tempDir, model, Scale, PaddedWidth, PaddedHeight, 0);
        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            -1
        );

        Assert.Null(
            ReloadFreshStore().ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );
    }

    [Fact]
    public void Recording_DoesNotTouchTheProfileTheStoreHandedOut()
    {
        // Load hands every caller the same object (the store's cached snapshot), and two pages of one
        // chapter plan — reading the per-shape measurements — while a third records what it measured.
        // A Dictionary read concurrent with a write is undefined behaviour, so the recorder must work
        // on a copy: the published object has to stay exactly as it was.
        (string model, long size) = SeedModelWithMeasurement();
        DeviceMemoryProfile published = _store.Load(_tempDir, _calibrator.BuildFingerprint(0))!;
        Assert.Empty(published.Models[DeviceMemoryProfile.ModelKey(model, size)].ObservedTilePeaks);

        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        Assert.Empty(published.Models[DeviceMemoryProfile.ModelKey(model, size)].ObservedTilePeaks);
        // ...while the measurement itself did reach the file.
        Assert.Equal(
            9604L,
            ReloadFreshStore().ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );
    }

    [Fact]
    public void Clone_IsIndependentOfTheProfileItCameFrom()
    {
        (string model, long size) = SeedModelWithMeasurement();
        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        DeviceMemoryProfile original = ReloadFreshStore();
        DeviceMemoryProfile copy = original.Clone();

        string key = DeviceMemoryProfile.ModelKey(model, size);
        copy.Models[key].ObservedTilePeaks[DeviceMemoryProfile.ObservedShapeKey(4, 128, 128)] = 1;
        copy.Models["added.onnx|1"] = new ModelMemoryProfile();
        copy.ActivationScale = 99;

        Assert.Single(original.Models[key].ObservedTilePeaks);
        Assert.False(original.Models.ContainsKey("added.onnx|1"));
        Assert.NotEqual(99, original.ActivationScale);
        // The nested map is copied too, not shared.
        Assert.NotSame(original.Models[key].ObservedTilePeaks, copy.Models[key].ObservedTilePeaks);
    }

    [Fact]
    public void CarryObservedPeaksOver_MovesTheMeasuredShapesOntoTheFreshMeasurement()
    {
        // The entry a re-measurement produces replaces the stored one wholesale (the store's own merge
        // layers per-model entries), so a forced recalibration would otherwise drop the footprints
        // measured at the shapes the pages actually ran.
        var previous = new ModelMemoryProfile();
        previous.ObservedTilePeaks[DeviceMemoryProfile.ObservedShapeKey(4, 640, 864)] = 9604L;
        previous.ObservedTilePeaks[DeviceMemoryProfile.ObservedShapeKey(4, 384, 384)] = 2100L;

        var measured = new ModelMemoryProfile { ActivationScale = 1.28 };

        DeviceMemoryCalibrator.CarryObservedPeaksOver(measured, previous);

        Assert.Equal(2, measured.ObservedTilePeaks.Count);
        Assert.Equal(
            9604L,
            measured.ObservedTilePeaks[DeviceMemoryProfile.ObservedShapeKey(4, 640, 864)]
        );

        // Nothing to carry, and nothing invented: a model measured for the first time keeps its own
        // empty map, and a null predecessor is not an error.
        var fresh = new ModelMemoryProfile();
        DeviceMemoryCalibrator.CarryObservedPeaksOver(fresh, previous: null);
        Assert.Empty(fresh.ObservedTilePeaks);
        DeviceMemoryCalibrator.CarryObservedPeaksOver(fresh, new ModelMemoryProfile());
        Assert.Empty(fresh.ObservedTilePeaks);
    }

    [Fact]
    public void ObservedTilePeaks_RoundTripThroughTheProfileFile()
    {
        (string model, long size) = SeedModelWithMeasurement();
        _calibrator.RecordObservedTilePeak(
            0,
            _tempDir,
            model,
            Scale,
            PaddedWidth,
            PaddedHeight,
            9604L
        );

        // The comparer and the values both have to survive the file, not just the in-memory snapshot.
        string json = File.ReadAllText(DeviceMemoryProfileStore.PathFor(_tempDir));
        Assert.Contains("observedTilePeaks", json, StringComparison.Ordinal);
        Assert.Contains(
            DeviceMemoryProfile.ObservedShapeKey(Scale, PaddedWidth, PaddedHeight),
            json,
            StringComparison.Ordinal
        );

        Assert.Equal(
            9604L,
            ReloadFreshStore().ObservedTilePeakFor(model, size, Scale, PaddedWidth, PaddedHeight)
        );
    }

    private (string Model, long Size) SeedModelWithMeasurement()
    {
        (string model, long size) = SeedModel(
            Path.Combine(_tempDir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx")
        );

        var profile = new DeviceMemoryProfile
        {
            Fingerprint = _calibrator.BuildFingerprint(0),
            ActivationScale = 0.9,
            DeviceWideMeasured = true,
        };
        profile.Models[DeviceMemoryProfile.ModelKey(model, size)] = new ModelMemoryProfile
        {
            ActivationScale = 0.9486,
            SessionReservationBytes = 80L * 1024 * 1024,
        };
        _store.Save(_tempDir, profile);
        return (model, size);
    }

    private static (string Model, long Size) SeedModel(string path)
    {
        File.WriteAllBytes(path, new byte[1024]);
        return (path, new FileInfo(path).Length);
    }

    /// <summary>
    /// Reads the profile back through a store that never saw this process's writes, so the assertions
    /// are about the file rather than about the in-memory snapshot.
    /// </summary>
    private DeviceMemoryProfile? ReloadFreshStoreOrNull() =>
        new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance).Load(
            _tempDir,
            _calibrator.BuildFingerprint(0)
        );

    /// <inheritdoc cref="ReloadFreshStoreOrNull"/>
    private DeviceMemoryProfile ReloadFreshStore()
    {
        DeviceMemoryProfile? profile = ReloadFreshStoreOrNull();
        Assert.NotNull(profile);
        return profile!;
    }
}
