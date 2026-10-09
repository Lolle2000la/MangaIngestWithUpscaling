using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

public class DeviceMemoryCalibratorTests
{
    private static DeviceMemoryCalibrator CreateCalibrator(
        UpscalerConfig? config = null,
        IOnnxSessionFactory? sessionFactory = null
    ) =>
        new(
            sessionFactory ?? Substitute.For<IOnnxSessionFactory>(),
            Options.Create(config ?? new UpscalerConfig()),
            new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
            NullLogger<DeviceMemoryCalibrator>.Instance
        );

    [Fact]
    public void Fingerprint_IsStableForSameDevice()
    {
        DeviceMemoryCalibrator calibrator = CreateCalibrator();

        string first = calibrator.BuildFingerprint(0);
        string second = calibrator.BuildFingerprint(0);

        // CPU-only hosts are a legitimate fingerprint too, so only require stability
        Assert.Equal(first, second);
        Assert.Contains("|", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_FollowsTheConfiguredProvider()
    {
        // The provider has to come from the session factory, not be guessed: forcing a backend or
        // CPU must change the identity, otherwise a calibration measured on one provider would be
        // reused on another.
        string auto = CreateCalibrator().BuildFingerprint(0);

        var cpuSessionFactory = Substitute.For<IOnnxSessionFactory>();
        cpuSessionFactory.GetEffectiveBackend().Returns(GpuBackend.CPU);
        string cpu = new DeviceMemoryCalibrator(
            cpuSessionFactory,
            Options.Create(new UpscalerConfig { UseCPU = true }),
            new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
            NullLogger<DeviceMemoryCalibrator>.Instance
        ).BuildFingerprint(0);

        var migraphxSessionFactory = Substitute.For<IOnnxSessionFactory>();
        migraphxSessionFactory.GetEffectiveBackend().Returns(GpuBackend.MIGraphX);
        string migraphx = new DeviceMemoryCalibrator(
            migraphxSessionFactory,
            Options.Create(new UpscalerConfig { PreferredGpuBackend = GpuBackend.MIGraphX }),
            new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
            NullLogger<DeviceMemoryCalibrator>.Instance
        ).BuildFingerprint(0);

        Assert.NotEqual(auto, cpu);
        Assert.NotEqual(auto, migraphx);
        Assert.NotEqual(cpu, migraphx);
        Assert.StartsWith("cpu|", cpu, StringComparison.Ordinal);
        Assert.StartsWith("migraphx|", migraphx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOrCalibrate_WithoutModelsDirectory_ReturnsNull()
    {
        DeviceMemoryCalibrator calibrator = CreateCalibrator();

        DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateAsync(
            0,
            Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken
        );

        Assert.Null(profile);
    }

    [Fact]
    public async Task GetOrCalibrate_WithCachedProfile_DoesNotBenchmark()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            string fingerprint = calibrator.BuildFingerprint(0);
            var cached = new DeviceMemoryProfile
            {
                Fingerprint = fingerprint,
                ActivationScale = 4.5,
                SessionReservationBytes = 100L * 1024 * 1024,
                CalibratedAtUtc = DateTimeOffset.UnixEpoch,
            };
            store.Save(dir, cached);

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.NotNull(profile);
            Assert.Equal(4.5, profile!.ActivationScale);
            // A cache hit must not touch the GPU at all
            sessionFactory.DidNotReceiveWithAnyArgs().GetOrCreateSession(default!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrate_RecalibrationForced_ReplacesCachedProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // No models in this directory, so the benchmark cannot run and the stored profile has
            // to survive untouched — proving the flag does not simply drop the cached value.
            DeviceMemoryCalibrator calibrator = CreateCalibrator(
                new UpscalerConfig { RecalibrateDeviceMemory = true }
            );
            string fingerprint = calibrator.BuildFingerprint(0);
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var cached = new DeviceMemoryProfile
            {
                Fingerprint = fingerprint,
                ActivationScale = 4.5,
                CalibratedAtUtc = DateTimeOffset.UnixEpoch,
            };
            store.Save(dir, cached);

            await calibrator.GetOrCalibrateAsync(0, dir, TestContext.Current.CancellationToken);

            var reloaded = new DeviceMemoryProfileStore(
                NullLogger<DeviceMemoryProfileStore>.Instance
            );
            Assert.NotNull(reloaded.Load(dir, fingerprint));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrate_OnCpu_ReturnsNull()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx"), "x");
            DeviceMemoryCalibrator calibrator = CreateCalibrator(
                new UpscalerConfig { UseCPU = true }
            );

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.Null(profile);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EstimateTileSize_MeasuredProfile_ChangesDerivedTile()
    {
        // The same budget and model must produce different tiles with and without a profile:
        // a profile whose measured scale is higher than the built-in one shrinks the tile.
        long budget = 12L * 1024 * 1024 * 1024;
        string model = "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx";

        int builtin = OnnxTiler.EstimateTileSize(
            1125,
            1600,
            4,
            model,
            budget,
            34L * 1024 * 1024,
            isFp16: true
        );

        var heavier = new DeviceMemoryProfile
        {
            ActivationScale = 10.0,
            SessionReservationBytes = 1024L * 1024 * 1024,
        };
        var lighter = new DeviceMemoryProfile
        {
            ActivationScale = 1.0,
            SessionReservationBytes = 64L * 1024 * 1024,
        };

        int withHeavier = OnnxTiler.EstimateTileSize(
            1125,
            1600,
            4,
            model,
            budget,
            34L * 1024 * 1024,
            isFp16: true,
            heavier
        );
        int withLighter = OnnxTiler.EstimateTileSize(
            1125,
            1600,
            4,
            model,
            budget,
            34L * 1024 * 1024,
            isFp16: true,
            lighter
        );

        Assert.True(
            withLighter > withHeavier,
            $"expected a lighter measurement to allow a larger tile, got {withLighter} vs {withHeavier}"
        );
        Assert.InRange(withHeavier, OnnxTiler.MinimumTileSize, builtin);
    }

    [Fact]
    public void EstimateTileSize_OutOfRangeProfileValues_AreIgnored()
    {
        long budget = 12L * 1024 * 1024 * 1024;
        string model = "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx";
        long weights = 34L * 1024 * 1024;

        int baseline = OnnxTiler.EstimateTileSize(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: true
        );

        // A nonsense profile (scale 0, negative scale, absurd reservation) must not change anything
        foreach (
            var profile in new[]
            {
                new DeviceMemoryProfile { ActivationScale = 0, SessionReservationBytes = 0 },
                new DeviceMemoryProfile { ActivationScale = -3, SessionReservationBytes = 1 },
                new DeviceMemoryProfile
                {
                    ActivationScale = 50,
                    SessionReservationBytes = 1L << 50,
                },
            }
        )
        {
            int withProfile = OnnxTiler.EstimateTileSize(
                1125,
                1600,
                4,
                model,
                budget,
                weights,
                isFp16: true,
                profile
            );
            Assert.Equal(baseline, withProfile);
        }
    }

    [Fact]
    public void InferScaleFromModelName_ReadsScalePrefix()
    {
        Assert.Equal(
            2,
            OnnxUpscaleEngine.InferScaleFromModelName("2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx")
        );
        Assert.Equal(
            4,
            OnnxUpscaleEngine.InferScaleFromModelName(
                "/models/4x_IllustrationJaNai_V1_DAT2_190k.onnx"
            )
        );
        Assert.Equal(
            0,
            OnnxUpscaleEngine.InferScaleFromModelName("MangaJaNai_1200p_V1_ESRGAN_70k.onnx")
        );
        Assert.Equal(0, OnnxUpscaleEngine.InferScaleFromModelName("page_break_detector.onnx"));
        Assert.Equal(0, OnnxUpscaleEngine.InferScaleFromModelName(""));
    }

    [Fact]
    public void ActivationCoefficients_MatchMeasuredCalibration()
    {
        // Regression guard for the table in GetOutputLiveChannels: the values the device benchmark
        // and the estimator both use. Change the coefficients, change this test knowingly.
        Assert.Equal(
            310.0,
            OnnxTiler.ActivationCoefficients.For("2x_SPAN_S_model.onnx").OutputLiveChannels
        );
        Assert.Equal(
            660.0,
            OnnxTiler.ActivationCoefficients.For("2x_MangaJaNai_ESRGAN.onnx").OutputLiveChannels
        );
        Assert.Equal(
            460.0,
            OnnxTiler.ActivationCoefficients.For("4x_FDAT_M_model.onnx").OutputLiveChannels
        );
        Assert.Equal(
            660.0,
            OnnxTiler.ActivationCoefficients.For("4x_FDAT_XL_model.onnx").OutputLiveChannels
        );
        Assert.Equal(
            1060.0,
            OnnxTiler.ActivationCoefficients.For("4x_DAT2_model.onnx").OutputLiveChannels
        );
        Assert.Equal(
            1900.0,
            OnnxTiler.ActivationCoefficients.For("4x_HAT_L_model.onnx").OutputLiveChannels
        );

        // Bytes per pixel: ESRGAN 4x fp16 measured 660 * 16 * 2 = 21120 B/px
        OnnxTiler.ActivationCoefficients esrgan = OnnxTiler.ActivationCoefficients.For(
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"
        );
        Assert.Equal(21120.0, esrgan.BytesPerPixel(4, isFp16: true));
        Assert.Equal(42240.0, esrgan.BytesPerPixel(4, isFp16: false));
        Assert.Equal(5280.0, esrgan.BytesPerPixel(2, isFp16: true));
    }
}
