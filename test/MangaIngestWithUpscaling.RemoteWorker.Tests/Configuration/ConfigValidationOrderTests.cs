using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Configuration;

/// <summary>
/// The worker fails fast on an invalid <see cref="UpscalerConfig"/> by resolving
/// <see cref="IOptions{TOptions}"/>.Value before its connection retry loop, because
/// <c>ValidateOnStart</c> only fires inside <c>Host.StartAsync</c> (which the retry loop runs before).
/// These tests pin that direct resolution — not host startup — is what triggers the validator.
/// </summary>
public class ConfigValidationOrderTests
{
    private static ServiceProvider BuildProvider(string memoryBudgetBytes)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:MemoryBudgetBytes"] = memoryBudgetBytes,
                }
            )
            .Build();

        return new ServiceCollection()
            .AddOptions<UpscalerConfig>()
            .Bind(configuration.GetSection(UpscalerConfig.Position))
            .ValidateOnStart()
            .Services.AddSingleton<IValidateOptions<UpscalerConfig>, UpscalerConfigValidator>()
            .BuildServiceProvider();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolvingOptionsDirectly_FailsFastOnAnInvalidSizeSetting()
    {
        // No host is started here: the worker must surface a bad size string before it ever reaches
        // app.Run()/Host.StartAsync, so an unreachable server cannot mask the configuration error.
        using ServiceProvider provider = BuildProvider("12 Gigabytes");

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<UpscalerConfig>>().Value;
        });

        Assert.Contains("Upscaler:MemoryBudgetBytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("12 Gigabytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolvingOptionsDirectly_AllowsAValidSizeSetting()
    {
        using ServiceProvider provider = BuildProvider("12 GiB");

        UpscalerConfig resolved = provider.GetRequiredService<IOptions<UpscalerConfig>>().Value;

        Assert.Equal(12884901888L, resolved.ResolvedMemoryBudgetBytes);
    }
}
