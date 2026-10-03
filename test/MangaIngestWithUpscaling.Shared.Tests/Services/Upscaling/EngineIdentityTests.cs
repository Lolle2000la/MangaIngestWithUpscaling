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
    public void ForUpscaler_ChangesWhenPreprocessingChanges()
    {
        UpscalerConfig baseline = Config();
        UpscalerConfig changed = Config();
        changed.EnableSmartDownscale = true;

        Assert.NotEqual(EngineIdentity.ForUpscaler(baseline), EngineIdentity.ForUpscaler(changed));
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
    public void ForUpscaler_IgnoresDeviceSelection()
    {
        // A CUDA box and a ROCm box with the same models/preprocessing produce the same pixels, so
        // device selection must not invalidate a chapter's spool on a cross-device hand-off.
        UpscalerConfig cuda = Config();
        UpscalerConfig rocm = Config();
        rocm.PreferredGpuBackend = GpuBackend.ROCm;
        rocm.SelectedDeviceIndex = 3;

        Assert.Equal(EngineIdentity.ForUpscaler(cuda), EngineIdentity.ForUpscaler(rocm));
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
    public void ForUpscaler_IgnoresInactivePreprocessingKnobs()
    {
        // null and 0 both mean "no max dimension", and the smart-downscale knobs only matter when the
        // feature is enabled; changing an inactive setting must not invalidate a spool.
        UpscalerConfig nullMax = Config();
        UpscalerConfig zeroMax = Config();
        zeroMax.MaxDimensionBeforeUpscaling = 0;
        Assert.Equal(EngineIdentity.ForUpscaler(nullMax), EngineIdentity.ForUpscaler(zeroMax));

        UpscalerConfig baseline = Config();
        UpscalerConfig changedKnob = Config();
        changedKnob.SmartDownscaleThreshold = 99.0;
        changedKnob.SmartDownscaleFactor = 0.5;
        Assert.Equal(EngineIdentity.ForUpscaler(baseline), EngineIdentity.ForUpscaler(changedKnob));

        // Once enabled, the knobs are part of the identity.
        UpscalerConfig enabled = Config();
        enabled.EnableSmartDownscale = true;
        UpscalerConfig enabledDifferent = Config();
        enabledDifferent.EnableSmartDownscale = true;
        enabledDifferent.SmartDownscaleThreshold = 99.0;
        Assert.NotEqual(
            EngineIdentity.ForUpscaler(enabled),
            EngineIdentity.ForUpscaler(enabledDifferent)
        );
    }

    private static UpscalerConfig Config() => new() { ModelsDirectory = "/nonexistent/models" };
}
