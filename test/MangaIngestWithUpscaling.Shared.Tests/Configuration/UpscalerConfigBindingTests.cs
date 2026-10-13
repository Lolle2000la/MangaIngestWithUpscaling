using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

public class UpscalerConfigBindingTests
{
    /// <summary>
    /// Binds the <c>Upscaler</c> section of a JSON document onto a fresh config, the same
    /// <c>Bind</c> call the options registration makes.
    /// </summary>
    private static UpscalerConfig BindJson(string upscalerJson)
    {
        UpscalerConfig config = new();
        new ConfigurationBuilder()
            .AddJsonStream(
                new MemoryStream(Encoding.UTF8.GetBytes($$"""{"Upscaler":{{upscalerJson}}}"""))
            )
            .Build()
            .GetSection(UpscalerConfig.Position)
            .Bind(config);
        return config;
    }

    [Fact]
    public void ImageFormatConversionRules_EmptyArrayDisablesTheBuiltInDefaults()
    {
        // An explicit [] has to mean "no conversion", not "keep the built-in defaults". The binder
        // does not touch a collection property whose section has no children, so an empty array is
        // surfaced as an empty scalar and the list's converter turns it into an empty list; without
        // that, [] would be indistinguishable from an absent key.
        UpscalerConfig config = BindJson("""{"ImageFormatConversionRules":[]}""");

        Assert.NotNull(config.ImageFormatConversionRules);
        Assert.Empty(config.ImageFormatConversionRules!);
        Assert.Empty(config.EffectiveImageFormatConversionRules);
    }

    [Fact]
    public void ImageFormatConversionRules_AbsentKeyUsesTheBuiltInDefaults()
    {
        // No key at all must keep the built-in PNG/AVIF -> JPG rules rather than disabling them.
        UpscalerConfig config = BindJson("""{"UseCPU":true}""");

        Assert.Null(config.ImageFormatConversionRules);
        Assert.Equal(
            UpscalerConfig.DefaultImageFormatConversionRules,
            config.EffectiveImageFormatConversionRules
        );
        Assert.NotEmpty(config.EffectiveImageFormatConversionRules);
    }

    [Fact]
    public void ImageFormatConversionRules_NonEmptyArrayBindsItsRules()
    {
        // The empty-array converter must not swallow the normal collection binding.
        UpscalerConfig config = BindJson(
            """{"ImageFormatConversionRules":[{"FromFormat":".png","ToFormat":".webp","Quality":80},{"FromFormat":".avif","ToFormat":".png"}]}"""
        );

        Assert.NotNull(config.ImageFormatConversionRules);
        Assert.Equal(2, config.ImageFormatConversionRules!.Count);
        Assert.Equal(".png", config.ImageFormatConversionRules[0].FromFormat);
        Assert.Equal(".webp", config.ImageFormatConversionRules[0].ToFormat);
        Assert.Equal(80, config.ImageFormatConversionRules[0].Quality);
        Assert.Equal(".avif", config.ImageFormatConversionRules[1].FromFormat);
        Assert.Equal(".png", config.ImageFormatConversionRules[1].ToFormat);
        Assert.Equal(2, config.EffectiveImageFormatConversionRules.Count);
    }

    [Fact]
    public async Task SelectedDeviceIndex_BindsFromConfiguration()
    {
        // SelectedDeviceIndex gates execution-provider selection, the VRAM budget and the engine
        // identity, so the value coming out of the options system must be the configured one and
        // not the field/property default — a default of 1 would silently keep the app on the
        // first accelerator.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:SelectedDeviceIndex"] = "0",
                }
            )
            .Build();

        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // The same registration both the app and the remote worker use.
                services
                    .AddOptions<UpscalerConfig>()
                    .Bind(configuration.GetSection(UpscalerConfig.Position))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<UpscalerConfig>, UpscalerConfigValidator>();
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        UpscalerConfig resolved = host
            .Services.GetRequiredService<IOptions<UpscalerConfig>>()
            .Value;

        Assert.Equal(0, resolved.SelectedDeviceIndex);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
