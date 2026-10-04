using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class EngineIdentityTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_IsStableForTheSameConfig()
    {
        UpscalerConfig config = Config();

        Assert.Equal(EngineIdentity.ForUpscaler(config), EngineIdentity.ForUpscaler(config));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_IgnoresPreprocessingBecauseTheServerOwnsIt()
    {
        // The server owns preprocessing and folds the effective options into the content identity, so
        // the worker's engine identity must not vary with its local preprocessing config: otherwise a
        // server-side change would go unnoticed, and a differing local config would needlessly reject
        // a spool on a hand-off.
        UpscalerConfig baseline = Config();
        UpscalerConfig changed = Config();
        changed.EnableSmartDownscale = true;
        changed.MaxDimensionBeforeUpscaling = 1600;
        changed.ImageFormatConversionRules =
        [
            new ImageFormatConversionRule { FromFormat = ".png", ToFormat = ".webp" },
        ];

        Assert.Equal(EngineIdentity.ForUpscaler(baseline), EngineIdentity.ForUpscaler(changed));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_ChangesWhenAModelFileChanges()
    {
        string directory = Directory.CreateTempSubdirectory("engine_models").FullName;
        try
        {
            string model = Path.Combine(directory, "model.pth");
            File.WriteAllBytes(model, new byte[] { 1, 2, 3 });
            UpscalerConfig config = Config();
            config.ModelsDirectory = directory;
            string before = EngineIdentity.ForUpscaler(config);

            File.WriteAllBytes(model, new byte[] { 1, 2, 3, 4, 5 });
            File.SetLastWriteTimeUtc(model, DateTime.UtcNow.AddSeconds(5));
            string after = EngineIdentity.ForUpscaler(config);

            Assert.NotEqual(before, after);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_DistinguishesBackendsButNotDeviceIndex()
    {
        // The accelerator backend is part of the identity: a CUDA and a ROCm run can produce different
        // pixels (different kernels/precision), so they must never mix. The device index within a
        // backend is not hashed, so a same-backend hand-off keeps the spool.
        UpscalerConfig cuda = Config();
        UpscalerConfig cudaOtherDevice = Config();
        cudaOtherDevice.SelectedDeviceIndex = 3;
        UpscalerConfig rocm = Config();
        rocm.PreferredGpuBackend = GpuBackend.ROCm;
        rocm.SelectedDeviceIndex = 3;

        Assert.Equal(EngineIdentity.ForUpscaler(cuda), EngineIdentity.ForUpscaler(cudaOtherDevice));
        Assert.NotEqual(EngineIdentity.ForUpscaler(cuda), EngineIdentity.ForUpscaler(rocm));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_DistinguishesCpuFromGpu()
    {
        // Device 0 is the CPU switch (the worker maps UseCPU to SelectedDeviceIndex = 0), so a CPU
        // run must not hash the same as a GPU run — otherwise CPU and GPU pages could mix.
        UpscalerConfig gpu = Config();
        gpu.SelectedDeviceIndex = 1;
        UpscalerConfig cpuViaIndex = Config();
        cpuViaIndex.SelectedDeviceIndex = 0;
        UpscalerConfig cpuViaFlag = Config();
        cpuViaFlag.SelectedDeviceIndex = 1;
        cpuViaFlag.UseCPU = true;

        Assert.NotEqual(EngineIdentity.ForUpscaler(gpu), EngineIdentity.ForUpscaler(cpuViaIndex));
        Assert.NotEqual(EngineIdentity.ForUpscaler(gpu), EngineIdentity.ForUpscaler(cpuViaFlag));
        // Both CPU spellings must agree.
        Assert.Equal(
            EngineIdentity.ForUpscaler(cpuViaIndex),
            EngineIdentity.ForUpscaler(cpuViaFlag)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_DistinguishesASameSizeFineTune()
    {
        // The fingerprint is relative path + size + a content prefix, so two models of the same size
        // but different bytes must not hash the same (a same-size fine-tune). A path+size-only
        // regression would pass this only if the prefix hash were dropped.
        string first = Directory.CreateTempSubdirectory("engine_models_a").FullName;
        string second = Directory.CreateTempSubdirectory("engine_models_b").FullName;
        try
        {
            byte[] a = new byte[256];
            byte[] b = new byte[256];
            a[0] = 1;
            b[0] = 2;
            File.WriteAllBytes(Path.Combine(first, "model.pth"), a);
            File.WriteAllBytes(Path.Combine(second, "model.pth"), b);

            UpscalerConfig firstConfig = Config();
            firstConfig.ModelsDirectory = first;
            UpscalerConfig secondConfig = Config();
            secondConfig.ModelsDirectory = second;

            Assert.NotEqual(
                EngineIdentity.ForUpscaler(firstConfig),
                EngineIdentity.ForUpscaler(secondConfig)
            );
        }
        finally
        {
            Directory.Delete(first, true);
            Directory.Delete(second, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_DistinguishesASameSizeFineTunePastTheFirstWindow()
    {
        // The sample covers the head, middle and tail, so a same-size file that diverges only past the
        // first 64 KiB window is still distinguished.
        string first = Directory.CreateTempSubdirectory("engine_models_head").FullName;
        string second = Directory.CreateTempSubdirectory("engine_models_tail").FullName;
        try
        {
            const int size = 512 * 1024;
            byte[] a = new byte[size];
            byte[] b = new byte[size];
            a[size - 1] = 1;
            b[size - 1] = 2;
            File.WriteAllBytes(Path.Combine(first, "model.pth"), a);
            File.WriteAllBytes(Path.Combine(second, "model.pth"), b);

            UpscalerConfig firstConfig = Config();
            firstConfig.ModelsDirectory = first;
            UpscalerConfig secondConfig = Config();
            secondConfig.ModelsDirectory = second;

            Assert.NotEqual(
                EngineIdentity.ForUpscaler(firstConfig),
                EngineIdentity.ForUpscaler(secondConfig)
            );
        }
        finally
        {
            Directory.Delete(first, true);
            Directory.Delete(second, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_IgnoresAllPreprocessingKnobs()
    {
        // Preprocessing is server-owned (see ForUpscaler_IgnoresPreprocessingBecauseTheServerOwnsIt),
        // so no local preprocessing setting affects the engine identity.
        UpscalerConfig baseline = Config();
        UpscalerConfig changed = Config();
        changed.MaxDimensionBeforeUpscaling = 1600;
        changed.EnableSmartDownscale = true;
        changed.SmartDownscaleThreshold = 99.0;
        changed.SmartDownscaleFactor = 0.5;

        Assert.Equal(EngineIdentity.ForUpscaler(baseline), EngineIdentity.ForUpscaler(changed));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForUpscaler_HashesTheResolvedBackendNotTheAutoPreference()
    {
        // The preference defaults to Auto, so two default deployments on different hardware would
        // share an identity unless the resolved backend is hashed. The provider passes the value the
        // Python environment actually installed.
        UpscalerConfig config = Config();
        Assert.Equal(GpuBackend.Auto, config.PreferredGpuBackend);

        Assert.NotEqual(
            EngineIdentity.ForUpscaler(config, GpuBackend.CUDA),
            EngineIdentity.ForUpscaler(config, GpuBackend.ROCm)
        );

        // Falls back to the preference when the environment is not prepared yet (tests, first start).
        Assert.Equal(
            EngineIdentity.ForUpscaler(config),
            EngineIdentity.ForUpscaler(config, GpuBackend.Auto)
        );
    }

    private static UpscalerConfig Config() => new() { ModelsDirectory = "/nonexistent/models" };
}
