using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// The calibration benchmark runs in a child process of the host's own executable.
/// <para>
/// The benchmark's two sessions leave device memory the execution provider never returns — measured on
/// an RX 9070 XT at 3012 MB for the fp16 ESRGAN — and the only thing that gives it back is the process
/// exiting. In the host that memory is resident while the first page is planned, and the page then
/// spends the safety margin on it: measured at 15636 MB of a 16304 MB card for a cold first page,
/// against 12668 MB once the profile exists. These tests pin the two halves that can be pinned without
/// a device: the command the child is spawned with, and the calibrator adopting what the child wrote
/// (and falling back when it wrote nothing).
/// </para>
/// </summary>
public class DeviceMemoryBenchmarkChildTests
{
    [Fact]
    public void IsCommand_MatchesTheBenchmarkCommandOnly()
    {
        Assert.True(DeviceMemoryBenchmarkHost.IsCommand([DeviceMemoryBenchmarkHost.CommandName]));
        Assert.True(
            DeviceMemoryBenchmarkHost.IsCommand([DeviceMemoryBenchmarkHost.CommandName, "x", "y"])
        );
        Assert.False(DeviceMemoryBenchmarkHost.IsCommand([]));
        Assert.False(DeviceMemoryBenchmarkHost.IsCommand(["--urls", "http://0.0.0.0:5091"]));
    }

    [Fact]
    public void EncodeConfig_RoundTripsTheConfigurationTheParentResolved()
    {
        // The child must measure the parent's configuration — the backend, the device index, the models
        // directory — not whatever its own working directory and environment produce.
        var config = new UpscalerConfig
        {
            ModelsDirectory = "/models/with spaces",
            SelectedDeviceIndex = 2,
            PreferredGpuBackend = GpuBackend.DirectML,
            MemoryBudgetBytes = "6GiB",
            UseFp16 = true,
        };

        string encoded = DeviceMemoryBenchmarkHost.EncodeConfig(config);
        var decoded = JsonSerializer.Deserialize<UpscalerConfig>(
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
        );

        Assert.NotNull(decoded);
        Assert.Equal(config.ModelsDirectory, decoded!.ModelsDirectory);
        Assert.Equal(config.SelectedDeviceIndex, decoded.SelectedDeviceIndex);
        Assert.Equal(config.PreferredGpuBackend, decoded.PreferredGpuBackend);
        Assert.Equal(config.MemoryBudgetBytes, decoded.MemoryBudgetBytes);
        Assert.Equal(config.UseFp16, decoded.UseFp16);
    }

    [Fact]
    public async Task RunAsync_WithAMalformedCommandLine_ReportsUsageWithoutMeasuring()
    {
        Assert.Equal(2, await DeviceMemoryBenchmarkHost.RunAsync([]));
        Assert.Equal(
            2,
            await DeviceMemoryBenchmarkHost.RunAsync([
                DeviceMemoryBenchmarkHost.CommandName,
                "only",
            ])
        );
    }

    [Fact]
    public async Task RunAsync_WithAnUndecodableConfiguration_Fails()
    {
        Assert.Equal(
            3,
            await DeviceMemoryBenchmarkHost.RunAsync([
                DeviceMemoryBenchmarkHost.CommandName,
                "not base64 !!",
                "a.onnx",
                "4",
                "0",
            ])
        );
    }

    [Fact]
    public async Task GetOrCalibrateForModel_AdoptsWhatTheChildProcessWrote()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_child_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            string model = Path.Combine(dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(model));
            long size = new FileInfo(model).Length;

            var runner = Substitute.For<IDeviceMemoryBenchmarkProcessRunner>();
            var calibrator = new DeviceMemoryCalibrator(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance,
                runner
            );
            string fingerprint = calibrator.BuildFingerprint(0);

            runner
                .RunAsync(
                    Arg.Any<UpscalerConfig>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ =>
                {
                    // What the child does: measure and write to the store both processes share.
                    DeviceMemoryProfile profile =
                        store.Load(dir, fingerprint)
                        ?? new DeviceMemoryProfile { Fingerprint = fingerprint };
                    profile.Models[DeviceMemoryProfile.ModelKey(model, size)] =
                        new ModelMemoryProfile
                        {
                            ActivationScale = 1.28,
                            SessionReservationBytes = 9L * 1024 * 1024,
                            MeasuredTiles = [192, 576],
                        };
                    store.Save(dir, profile);
                    return Task.FromResult(true);
                });

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateForModelAsync(
                0,
                dir,
                model,
                4,
                TestContext.Current.CancellationToken
            );

            Assert.NotNull(profile);
            Assert.Equal(1.28, profile!.ScaleFor(model, size));
            Assert.Equal(9L * 1024 * 1024, profile.ReservationFor(model, size));
            // The child did the measuring, so this process never built a session for it.
            sessionFactory.DidNotReceiveWithAnyArgs().GetOrCreateSession(default!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrateForModel_WithAChildThatReportedSuccessButWroteNothing_MeasuresHere()
    {
        // A child that cannot write the store must not leave the model measured only in its own
        // process: this process would then plan the model from the built-in coefficients.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_child_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            string model = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(model));

            var runner = Substitute.For<IDeviceMemoryBenchmarkProcessRunner>();
            runner
                .RunAsync(
                    Arg.Any<UpscalerConfig>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult(true));

            var calibrator = new DeviceMemoryCalibrator(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance,
                runner
            );

            await calibrator.GetOrCalibrateForModelAsync(
                0,
                dir,
                model,
                4,
                TestContext.Current.CancellationToken
            );

            // The in-process path ran: it asks for the model's session to read its input type.
            sessionFactory.ReceivedWithAnyArgs().GetOrCreateSession(default!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrateForModel_WithAChildThatCouldNotRun_MeasuresHere()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_child_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var sessionFactory = Substitute.For<IOnnxSessionFactory>();
            string model = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            TinyOnnxModel.WriteNearestUpscaler(dir, scale: 4, fileName: Path.GetFileName(model));

            var runner = Substitute.For<IDeviceMemoryBenchmarkProcessRunner>();
            runner
                .RunAsync(
                    Arg.Any<UpscalerConfig>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult(false));

            var calibrator = new DeviceMemoryCalibrator(
                sessionFactory,
                Options.Create(new UpscalerConfig()),
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance,
                runner
            );

            await calibrator.GetOrCalibrateForModelAsync(
                0,
                dir,
                model,
                4,
                TestContext.Current.CancellationToken
            );

            sessionFactory.ReceivedWithAnyArgs().GetOrCreateSession(default!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOrCalibrateForModel_OnCpu_DoesNotSpawnAChild()
    {
        // There is no device budget to fill in CPU mode, so the calibration is skipped entirely —
        // and a child process would be a whole extra start-up for nothing.
        string dir = Path.Combine(Path.GetTempPath(), $"devmem_child_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string model = Path.Combine(dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx");
            File.WriteAllBytes(model, new byte[1024]);

            var runner = Substitute.For<IDeviceMemoryBenchmarkProcessRunner>();
            var calibrator = new DeviceMemoryCalibrator(
                Substitute.For<IOnnxSessionFactory>(),
                Options.Create(new UpscalerConfig { ModelsDirectory = dir, UseCPU = true }),
                new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
                NullLogger<DeviceMemoryCalibrator>.Instance,
                runner
            );

            await calibrator.GetOrCalibrateForModelAsync(
                0,
                dir,
                model,
                4,
                TestContext.Current.CancellationToken
            );

            await runner
                .DidNotReceiveWithAnyArgs()
                .RunAsync(
                    default!,
                    default!,
                    default,
                    default,
                    TestContext.Current.CancellationToken
                );
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
