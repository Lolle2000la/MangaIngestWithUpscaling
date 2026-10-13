using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
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
    public async Task GetOrCalibrateForModel_AFailedModelIsNotRetriedButDoesNotBlockAnother()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            // A session the factory cannot produce is the same failure an unwritable models volume
            // ends in, without needing one to set up.
            sessionFactory.GetOrCreateSession(Arg.Any<string>()).Returns((InferenceSession)null!);

            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            string first = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            string second = Path.Combine(dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(first));
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(second));

            Assert.Null(
                await calibrator.GetOrCalibrateForModelAsync(
                    0,
                    dir,
                    first,
                    4,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Null(
                await calibrator.GetOrCalibrateForModelAsync(
                    0,
                    dir,
                    first,
                    4,
                    TestContext.Current.CancellationToken
                )
            );
            // The first model's failure must not stop a different model from being measured: the
            // "already tried" memory is keyed per model, not per device.
            Assert.Null(
                await calibrator.GetOrCalibrateForModelAsync(
                    0,
                    dir,
                    second,
                    4,
                    TestContext.Current.CancellationToken
                )
            );

            // One attempt for each model, not one per page for the failing one.
            sessionFactory.Received(2).GetOrCreateSession(Arg.Any<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrateForModel_WithAMeasuredModelInTheStore_DoesNotTouchTheGpu()
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

            string model = Path.Combine(dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(model));
            long size = new FileInfo(model).Length;

            string fingerprint = calibrator.BuildFingerprint(0);
            var cached = new DeviceMemoryProfile
            {
                Fingerprint = fingerprint,
                ActivationScale = 0.90,
            };
            cached.Models[DeviceMemoryProfile.ModelKey(model, size)] = new ModelMemoryProfile
            {
                ActivationScale = 1.28,
                CalibratedAtUtc = DateTimeOffset.UnixEpoch,
            };
            store.Save(dir, cached);

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateForModelAsync(
                0,
                dir,
                model,
                4,
                TestContext.Current.CancellationToken
            );

            Assert.NotNull(profile);
            Assert.Equal(1.28, profile!.ScaleFor(model, size));
            // A stored per-model measurement must not touch the GPU at all.
            sessionFactory.DidNotReceiveWithAnyArgs().GetOrCreateSession(default!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    public async Task GetOrCalibrate_AfterAFailedBenchmark_DoesNotRetryItForEveryPage()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();

            // A models directory the profile cannot be written back to (a read-only volume, a
            // container running as an arbitrary uid) makes the benchmark come back empty. _unsupported
            // is meant to stop that from being re-learned on every page; without it being read, each
            // page paid three benchmark inferences plus a session teardown to reach the same null.
            // A session that cannot be created makes the benchmark fail the same way, without
            // needing an unwritable volume to set up.
            // A session the factory cannot produce is the same failure an unwritable models volume
            // ends in, without needing one to set up.
            sessionFactory
                .GetOrCreateSession(Arg.Any<string>())
                .Returns((InferenceSession)null!);

            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            // Named for what the calibrator looks for (a small ESRGAN) and so the scale comes off the
            // name, which is what makes it pick this file at all.
            TinyOnnxModel.WriteNearestUpscaler(
                dir,
                scale: 4,
                fileName: "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"
            );

            DeviceMemoryProfile? first = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );
            DeviceMemoryProfile? second = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.Null(first);
            Assert.Null(second);
            // The benchmark ran once, not once per page.
            sessionFactory.Received(1).GetOrCreateSession(Arg.Any<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrate_WithRecalibrateForced_BenchmarksOnceForTheWholeProcess()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            // A session the factory cannot produce is the same failure an unwritable models volume
            // ends in, without needing one to set up.
            sessionFactory.GetOrCreateSession(Arg.Any<string>()).Returns((InferenceSession)null!);

            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig { RecalibrateDeviceMemory = true }),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            TinyOnnxModel.WriteNearestUpscaler(
                dir,
                scale: 4,
                fileName: "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"
            );

            // RecalibrateDeviceMemory means "on the next upscale", not "on every upscale": the first
            // call re-measures, and the rest of the process takes the normal path (here: the now
            // remembered failure), so the pages of one chapter do not each pay the benchmark again.
            DeviceMemoryProfile? first = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );
            sessionFactory.Received(1).GetOrCreateSession(Arg.Any<string>());

            DeviceMemoryProfile? second = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );
            DeviceMemoryProfile? third = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.Null(first);
            Assert.Null(second);
            Assert.Null(third);
            sessionFactory.Received(1).GetOrCreateSession(Arg.Any<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrate_WithRecalibrateForced_AfterTheOneShotUsesTheCachedProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A profile this device has already been measured with, plus a benchmark that cannot
            // run: the forced first call must ignore the cached value and re-measure, and the call
            // after it must read the cached value without touching the GPU again.
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            sessionFactory.GetOrCreateSession(Arg.Any<string>()).Returns((InferenceSession)null!);

            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig { RecalibrateDeviceMemory = true }),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            TinyOnnxModel.WriteNearestUpscaler(
                dir,
                scale: 4,
                fileName: "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"
            );

            string fingerprint = calibrator.BuildFingerprint(0);
            store.Save(
                dir,
                new DeviceMemoryProfile
                {
                    Fingerprint = fingerprint,
                    ActivationScale = 4.5,
                    SessionReservationBytes = 100L * 1024 * 1024,
                    CalibratedAtUtc = DateTimeOffset.UnixEpoch,
                }
            );

            DeviceMemoryProfile? forced = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );
            sessionFactory.Received(1).GetOrCreateSession(Arg.Any<string>());

            DeviceMemoryProfile? after = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            // The one-shot was spent on the first call: the re-measurement did not replace the stored
            // profile (this benchmark cannot run), so both calls return the measurement that is on
            // disk and the next upscale still gets it from the store for free. Force re-measures the
            // requested key; it does not throw the rest of the profile away, because the store
            // replaces the whole device entry on save.
            Assert.NotNull(forced);
            Assert.Equal(4.5, forced!.ActivationScale);
            Assert.NotNull(after);
            Assert.Equal(4.5, after!.ActivationScale);
            sessionFactory.Received(1).GetOrCreateSession(Arg.Any<string>());
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

    [Theory]
    [InlineData(nameof(UpscalerConfig.UseCPU))]
    [InlineData(nameof(UpscalerConfig.SelectedDeviceIndex))]
    [InlineData(nameof(UpscalerConfig.PreferredGpuBackend))]
    public async Task GetOrCalibrate_OnAnyCpuSpelling_ReturnsNullWithoutAskingForASession(
        string spelling
    )
    {
        // The CPU rule is OnnxSessionFactory.ShouldUseCpuProvider's, not "UseCPU": SelectedDeviceIndex
        // <= 0 and PreferredGpuBackend = CPU are the other two spellings, and checking only UseCPU ran
        // the GPU benchmark — and wrote a bogus profile — on a host that never creates a GPU session.
        // The session assertion is what makes this observable: a failed benchmark also returns null,
        // so Assert.Null alone would stay green for the GPU path.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "2x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx"), "x");

            var config = new UpscalerConfig();
            switch (spelling)
            {
                case nameof(UpscalerConfig.UseCPU):
                    config.UseCPU = true;
                    break;
                case nameof(UpscalerConfig.SelectedDeviceIndex):
                    config.SelectedDeviceIndex = 0;
                    break;
                case nameof(UpscalerConfig.PreferredGpuBackend):
                    config.PreferredGpuBackend = GpuBackend.CPU;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null);
            }

            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            DeviceMemoryCalibrator calibrator = CreateCalibrator(config, sessionFactory);

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.Null(profile);
            sessionFactory.DidNotReceive().GetOrCreateSession(Arg.Any<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PlanTileSplit_MeasuredProfile_ChangesDerivedGrid()
    {
        // The same budget and model must produce different grids with and without a profile:
        // a profile whose measured scale is higher than the built-in one shrinks the tiles.
        long budget = 12L * 1024 * 1024 * 1024;
        string model = "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx";
        long weights = 34L * 1024 * 1024;

        OnnxTiler.TileSplit builtin = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: true
        );

        var heavier = new DeviceMemoryProfile
        {
            ActivationScale = 10.0,
            SessionReservationBytes = 1024L * 1024 * 1024,
            DeviceWideMeasured = true,
        };
        var lighter = new DeviceMemoryProfile
        {
            ActivationScale = 1.0,
            SessionReservationBytes = 64L * 1024 * 1024,
            DeviceWideMeasured = true,
        };

        OnnxTiler.TileSplit withHeavier = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: true,
            heavier
        );
        OnnxTiler.TileSplit withLighter = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: true,
            lighter
        );

        Assert.True(
            withLighter.TotalPaddedPixels < withHeavier.TotalPaddedPixels,
            $"expected a lighter measurement to do less work, got {withLighter.TotalPaddedPixels} vs {withHeavier.TotalPaddedPixels}"
        );
        Assert.True(
            withHeavier.TotalPaddedPixels >= builtin.TotalPaddedPixels,
            $"expected a heavier measurement to do at least as much work as the built-in, got {withHeavier.TotalPaddedPixels} vs {builtin.TotalPaddedPixels}"
        );
    }

    [Fact]
    public void PlanTileSplit_OutOfRangeProfileValues_AreIgnored()
    {
        long budget = 12L * 1024 * 1024 * 1024;
        string model = "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx";
        long weights = 34L * 1024 * 1024;

        OnnxTiler.TileSplit baseline = OnnxTiler.PlanTileSplit(
            1125,
            1600,
            4,
            model,
            budget,
            weights,
            isFp16: true
        );

        // A nonsense profile (scale 0, negative scale, absurd reservation) must not change anything.
        // The measured flag is set so the profiles are actually consulted — without it they would be
        // skipped as "no measurement" and the range checks these values exist to exercise would go
        // untested.
        foreach (
            var profile in new[]
            {
                new DeviceMemoryProfile
                {
                    ActivationScale = 0,
                    SessionReservationBytes = 0,
                    DeviceWideMeasured = true,
                },
                new DeviceMemoryProfile
                {
                    ActivationScale = -3,
                    SessionReservationBytes = 1,
                    DeviceWideMeasured = true,
                },
                new DeviceMemoryProfile
                {
                    ActivationScale = 50,
                    SessionReservationBytes = 1L << 50,
                    DeviceWideMeasured = true,
                },
            }
        )
        {
            OnnxTiler.TileSplit withProfile = OnnxTiler.PlanTileSplit(
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
    public async Task GetOrCalibrate_DuringBenchmark_EntersAnInferenceScope()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // The benchmark measures device memory through a live session, so it must hold an inference
            // scope for its duration: without one, a concurrent upscale could evict the session at
            // capacity or invalidate it on its own failure and dispose it mid-Run. The factory hands
            // back a real tiny session (not the null the retry tests use), so the benchmark actually
            // runs both measurement points rather than failing at the first session use — which is what
            // makes the scope count below a read-out of the measurement scopes and not just the
            // input-type probe. The count is one for that probe plus one per measurement point; drop the
            // scope from MeasurePoint and it falls to one, which this assertion catches.
            string modelPath = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            TinyOnnxModel.WriteNearestUpscaler(
                dir,
                scale: 4,
                fp16: false,
                fileName: Path.GetFileName(modelPath)
            );

            using var sessionFactory = new ScopeCountingSessionFactory(modelPath);
            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateAsync(
                0,
                dir,
                TestContext.Current.CancellationToken
            );

            Assert.Null(profile);
            // One scope for probing the input type, then one for each of the two benchmark tiles.
            Assert.Equal(3, sessionFactory.ScopeCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrateForModel_CancelledToken_AbortsTheBenchmarkBeforeMeasuring()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string model = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(model));

            using var cts = new CancellationTokenSource();
            // Cancel the instant the benchmark asks for its first session — reading the input type,
            // before either measurement point. A pre-cancelled token would abort at the semaphore wait
            // instead, which says nothing about the benchmark; this cancellation lands inside it, so
            // only the token check at the first measure boundary can stop it.
            using var sessionFactory = new CancelOnFirstSessionFactory(model, cts);
            DeviceMemoryCalibrator calibrator = new(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
                NullLogger<DeviceMemoryCalibrator>.Instance
            );

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                calibrator.GetOrCalibrateForModelAsync(0, dir, model, 4, cts.Token)
            );

            // Cancelled before the first measurement point: only the input-type probe asked for a
            // session, no benchmark tile ran.
            Assert.Equal(1, sessionFactory.SessionsCreated);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ActivationCoefficients_MatchMeasuredCalibration()
    {
        // Regression guard for the table in GetOutputLiveChannels: the values the device benchmark
        // and the estimator both use. Change the coefficients, change this test knowingly.
        // Both columns are pinned: a single fp16-fitted value under-estimated every transformer
        // export that actually runs on WebGPU (the fp16 ones need bf16) by up to 34%.
        Assert.Equal((315.0, 158.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.Span));
        Assert.Equal((598.0, 463.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.Esrgan));
        Assert.Equal((462.0, 590.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.FdatM));
        Assert.Equal((660.0, 882.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.FdatXl));
        Assert.Equal((1060.0, 1167.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.Dat2));
        Assert.Equal((1900.0, 2055.0), OnnxTiler.GetOutputLiveChannels(ModelArchitecture.HatL));

        // The name-based lookup has to reach the same table.
        Assert.Equal(
            598.0,
            OnnxTiler.ActivationCoefficients.For("2x_MangaJaNai_ESRGAN.onnx").Fp16Channels
        );
        Assert.Equal(
            590.0,
            OnnxTiler.ActivationCoefficients.For("4x_FDAT_M_model.onnx").Fp32Channels
        );

        // Bytes per pixel follows the precision: the fp16 ESRGAN was measured at 19140 B/px and the
        // fp32 at 29600, not at the 21120/42240 an element-size scaling would give.
        OnnxTiler.ActivationCoefficients esrgan = OnnxTiler.ActivationCoefficients.For(
            "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"
        );
        Assert.Equal(598.0 * 16 * 2, esrgan.BytesPerPixel(4, isFp16: true));
        Assert.Equal(463.0 * 16 * 4, esrgan.BytesPerPixel(4, isFp16: false));
        Assert.Equal(598.0 * 4 * 2, esrgan.BytesPerPixel(2, isFp16: true));

        // The fp32 transformer is the case that spilled: its coefficient must be the measured one,
        // not the fp16 value applied to a 4-byte element.
        OnnxTiler.ActivationCoefficients fdat = OnnxTiler.ActivationCoefficients.For(
            "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx"
        );
        Assert.Equal(590.0 * 16 * 4, fdat.BytesPerPixel(4, isFp16: false));
    }

    [Fact]
    public void UnknownArchitecture_FallsBackToTheParameterCountForBothPrecisions()
    {
        // An unrecognised model is never under-estimated, and the fp32 arm is not smaller than the
        // fp16 one just because the same formula is used: the fp32 export is about twice the size.
        var small = OnnxTiler.GetOutputLiveChannels(ModelArchitecture.Unknown, 4L * 1024 * 1024);
        var large = OnnxTiler.GetOutputLiveChannels(ModelArchitecture.Unknown, 64L * 1024 * 1024);

        Assert.Equal(small.Fp16, small.Fp32);
        Assert.True(large.Fp16 > small.Fp16);
    }

    /// <summary>
    /// Records every inference scope entry and hands out a real session loaded from
    /// <paramref name="modelPath"/>, so the benchmark runs to completion and every scope it takes —
    /// the input-type probe and the two measurement points — is counted.
    /// </summary>
    private sealed class ScopeCountingSessionFactory(string modelPath) : IOnnxSessionFactory
    {
        private InferenceSession? _session;

        public int ScopeCount { get; private set; }

        public InferenceSession GetOrCreateSession(string path) =>
            _session ??= new InferenceSession(modelPath);

        public InferenceSession CreateSession(string path) => new(modelPath);

        public void InvalidateSession(string modelPath)
        {
            _session?.Dispose();
            _session = null;
        }

        public void InvalidateAllSessions() => InvalidateSession(modelPath);

        public GpuBackend GetEffectiveBackend() => GpuBackend.CPU;

        public IDisposable EnterInferenceScope()
        {
            ScopeCount++;
            return new NoOpScope();
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }

        private sealed class NoOpScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Hands out a real tiny session, but cancels the supplied source the first time one is asked for,
    /// so the benchmark is cancelled from inside itself — after the semaphore wait and before the first
    /// measurement point. <see cref="IOnnxSessionFactory.EnterInferenceScope"/> and
    /// <see cref="IOnnxSessionFactory.ReleaseCachedSessions"/> keep their default implementations.
    /// </summary>
    private sealed class CancelOnFirstSessionFactory(
        string modelPath,
        CancellationTokenSource cancellation
    ) : IOnnxSessionFactory
    {
        private readonly Lock _lock = new();
        private InferenceSession? _session;
        private bool _cancelled;

        public int SessionsCreated { get; private set; }

        public InferenceSession GetOrCreateSession(string path)
        {
            lock (_lock)
            {
                SessionsCreated++;
                if (!_cancelled)
                {
                    _cancelled = true;
                    cancellation.Cancel();
                }

                return _session ??= new InferenceSession(modelPath);
            }
        }

        public InferenceSession CreateSession(string path) => new(modelPath);

        public void InvalidateSession(string path)
        {
            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
            }
        }

        public void InvalidateAllSessions() => InvalidateSession(modelPath);

        public GpuBackend GetEffectiveBackend() => GpuBackend.CPU;

        public void Dispose()
        {
            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
            }
        }
    }
}
