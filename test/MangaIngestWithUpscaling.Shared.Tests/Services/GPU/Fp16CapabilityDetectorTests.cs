using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.GPU;

public class Fp16CapabilityDetectorTests
{
    [Fact]
    public void IsFp16Supported_WhenUseCpuIsTrue_ReturnsFalse()
    {
        bool result = Fp16CapabilityDetector.IsFp16Supported(
            resolvedBackend: GpuBackend.Auto,
            acceleratorIndex: 0,
            forceCpu: true
        );
        Assert.False(result);
    }

    [Fact]
    public void IsFp16Supported_WhenPreferredBackendIsCpu_ReturnsFalse()
    {
        bool result = Fp16CapabilityDetector.IsFp16Supported(
            resolvedBackend: GpuBackend.CPU,
            acceleratorIndex: 0,
            forceCpu: false
        );
        Assert.False(result);
    }

    [Fact]
    public void UpscalerConfig_ResolvedUseFp16_WhenExplicitlyTrue_ReturnsTrue()
    {
        var config = new UpscalerConfig { UseFp16 = true, UseCPU = true };
        Assert.True(config.ResolvedUseFp16);
    }

    [Fact]
    public void UpscalerConfig_ResolvedUseFp16_WhenExplicitlyFalse_ReturnsFalse()
    {
        var config = new UpscalerConfig
        {
            UseFp16 = false,
            UseCPU = false,
            SelectedDeviceIndex = 1,
        };
        Assert.False(config.ResolvedUseFp16);
    }

    [Theory]
    [InlineData(true, 1, GpuBackend.Auto)]
    [InlineData(false, 0, GpuBackend.Auto)]
    [InlineData(false, 1, GpuBackend.CPU)]
    public void UpscalerConfig_ResolvedUseFp16_WhenNullAndTargetingCpu_ReturnsFalse(
        bool useCpu,
        int deviceIndex,
        GpuBackend backend
    )
    {
        var config = new UpscalerConfig
        {
            UseFp16 = null,
            UseCPU = useCpu,
            SelectedDeviceIndex = deviceIndex,
            PreferredGpuBackend = backend,
        };
        Assert.False(config.ResolveUseFp16(backend));
    }

    [Fact]
    public void UpscalerConfig_ResolvedUseFp16_WhenNull_MatchesDetectorResult()
    {
        var config = new UpscalerConfig
        {
            UseFp16 = null,
            UseCPU = false,
            SelectedDeviceIndex = 1,
            PreferredGpuBackend = GpuBackend.Auto,
        };

        bool expected = Fp16CapabilityDetector.IsFp16Supported(
            resolvedBackend: GpuBackend.Auto,
            acceleratorIndex: 0,
            forceCpu: false
        );

        Assert.Equal(expected, config.ResolveUseFp16(GpuBackend.Auto));
    }

    [Fact]
    public void EngineIdentity_ForUpscaler_ReflectsResolvedFp16()
    {
        var configAuto = new UpscalerConfig
        {
            UseFp16 = null,
            UseCPU = true,
            SelectedDeviceIndex = 0,
        };

        var configExplicitFalse = new UpscalerConfig
        {
            UseFp16 = false,
            UseCPU = true,
            SelectedDeviceIndex = 0,
        };

        string hashAuto = EngineIdentity.ForUpscaler(configAuto);
        string hashExplicitFalse = EngineIdentity.ForUpscaler(configExplicitFalse);

        Assert.Equal(hashExplicitFalse, hashAuto);
    }
}
