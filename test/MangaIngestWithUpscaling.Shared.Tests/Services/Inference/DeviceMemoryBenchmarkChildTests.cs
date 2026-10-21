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
    public async Task RunAsync_WithAChildThatNeverExits_KillsItAndFallsBackInsteadOfCancelling()
    {
        // The deadline exists so a child that never reaches its own exit cannot hold the page. It has
        // to read as "the benchmark failed" — the caller measures in this process — and not as a
        // cancellation the page never asked for.
        string script = Path.Combine(Path.GetTempPath(), $"hang_{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\nsleep 60\n",
            TestContext.Current.CancellationToken
        );
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        else
        {
            Assert.Skip("the hanging child is a shell script");
        }
        try
        {
            var runner = new DeviceMemoryBenchmarkProcessRunner(
                NullLogger<DeviceMemoryBenchmarkProcessRunner>.Instance,
                script,
                TimeSpan.FromMilliseconds(250)
            );

            bool measured = await runner.RunAsync(
                new UpscalerConfig(),
                "a.onnx",
                4,
                0,
                TestContext.Current.CancellationToken
            );

            Assert.False(measured);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void TryKill_OnAProcessThatWasNeverStarted_DoesNotThrow()
    {
        // The teardown of an optimisation must not fail a page: HasExited throws on a process that was
        // never started, and Kill throws on one that exited between the check and the call.
        var runner = new DeviceMemoryBenchmarkProcessRunner(
            NullLogger<DeviceMemoryBenchmarkProcessRunner>.Instance
        );

        runner.TryKill(new System.Diagnostics.Process());
    }

    [Fact]
    public void ChildCommand_HandsTheAssemblyToTheDotnetMuxer()
    {
        // The production container starts the app as `dotnet MangaIngestWithUpscaling.dll`, so
        // Environment.ProcessPath is the muxer, which does not know the benchmark option. Without the
        // assembly in front of it the child could never run there, and every calibration would silently
        // fall back to measuring in the host — which is the cost this feature exists to avoid.
        (string file, string[] prefix) = DeviceMemoryBenchmarkProcessRunner.ChildCommand(
            "/usr/share/dotnet/dotnet",
            "/app/MangaIngestWithUpscaling.dll"
        );
        Assert.Equal("/usr/share/dotnet/dotnet", file);
        Assert.Equal(new[] { "/app/MangaIngestWithUpscaling.dll" }, prefix);

        // An apphost (a published executable, or a single-file build) is the application itself, so
        // there is nothing to put in front of it — the entry assembly's own path is the dll next to it.
        (file, prefix) = DeviceMemoryBenchmarkProcessRunner.ChildCommand(
            "/app/MangaIngestWithUpscaling",
            "/app/MangaIngestWithUpscaling.dll"
        );
        Assert.Equal("/app/MangaIngestWithUpscaling", file);
        Assert.Empty(prefix);

        // Windows spells the muxer dotnet.exe; the file-name comparison is case-insensitive, and the
        // path separator is the platform's, exactly as it is for Environment.ProcessPath.
        Assert.Equal(
            "C:\\app\\App.dll",
            Assert.Single(
                DeviceMemoryBenchmarkProcessRunner
                    .ChildCommand("dotnet.exe", "C:\\app\\App.dll")
                    .Prefix
            )
        );
        Assert.Empty(DeviceMemoryBenchmarkProcessRunner.ChildCommand("dotnet", null).Prefix);
        Assert.Empty(
            DeviceMemoryBenchmarkProcessRunner.ChildCommand("dotnet", string.Empty).Prefix
        );
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

            // A profile that exists but has no measurement for this model: that is what makes the
            // calibrator measure at all, and it is also the object the store hands to every caller.
            var seeded = new DeviceMemoryProfile { Fingerprint = fingerprint };
            seeded.Models[DeviceMemoryProfile.ModelKey("other.onnx", 1)] = new ModelMemoryProfile
            {
                ActivationScale = 1.1,
            };
            store.Save(dir, seeded);
            DeviceMemoryProfile published = store.Load(dir, fingerprint)!;

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
                    DeviceMemoryProfile written =
                        store.Load(dir, fingerprint)?.Clone()
                        ?? new DeviceMemoryProfile { Fingerprint = fingerprint };
                    written.Models[DeviceMemoryProfile.ModelKey(model, size)] =
                        new ModelMemoryProfile
                        {
                            ActivationScale = 1.28,
                            SessionReservationBytes = 9L * 1024 * 1024,
                            MeasuredTiles = [192, 576],
                        };
                    store.Save(dir, written);
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
            // And the object the store published before the measurement is untouched: the calibration
            // writes to a copy (DeviceMemoryProfile.Clone), because another page may be reading that
            // same object while it plans.
            Assert.False(
                published.Models.ContainsKey(DeviceMemoryProfile.ModelKey(model, size)),
                "the calibration mutated the profile the store had handed out"
            );
            // The adopted entry is a copy as well: it comes from the store's object, and this profile is
            // what the caller writes to next.
            Assert.NotSame(
                store.Load(dir, fingerprint)!.Models[DeviceMemoryProfile.ModelKey(model, size)],
                profile.Models[DeviceMemoryProfile.ModelKey(model, size)]
            );
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
