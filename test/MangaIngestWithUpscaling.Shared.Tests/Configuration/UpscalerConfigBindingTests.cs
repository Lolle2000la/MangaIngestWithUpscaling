using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

public class UpscalerConfigBindingTests
{
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
