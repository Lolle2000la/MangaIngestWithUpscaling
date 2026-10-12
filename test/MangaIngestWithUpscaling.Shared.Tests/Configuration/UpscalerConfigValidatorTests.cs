using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

public class UpscalerConfigValidatorTests
{
    private static UpscalerConfig Bind(params (string Key, string? Value)[] settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();
        return configuration.GetSection(UpscalerConfig.Position).Get<UpscalerConfig>()!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4GiB")]
    [InlineData("1073741824")]
    [InlineData("0")]
    [InlineData("700MiB")]
    [InlineData("1.5 GiB")]
    public void AcceptsUnsetAndValidSizes(string? value) =>
        Assert.Empty(new UpscalerConfig { MemoryBudgetBytes = value }.FindInvalidSizeSettings());

    [Theory]
    [InlineData("4GiBX")]
    [InlineData("12 Gigabytes")]
    [InlineData("4 Gi")]
    [InlineData("GB")]
    [InlineData("-4GiB")]
    [InlineData("1.2.3 GiB")]
    public void RejectsUnparseableSizes(string value)
    {
        List<string> invalid = new UpscalerConfig { MemoryBudgetBytes = value }
            .FindInvalidSizeSettings()
            .ToList();

        Assert.Single(invalid);
        Assert.Contains(
            $"Upscaler:MemoryBudgetBytes = \"{value}\"",
            invalid[0],
            StringComparison.Ordinal
        );
        Assert.Contains("expected a byte count or a size", invalid[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateResult_FailsForAnUnparseableSize()
    {
        var validator = new UpscalerConfigValidator();
        ValidateOptionsResult result = validator.Validate(
            null,
            new UpscalerConfig { MemoryBudgetBytes = "4GiBX" }
        );

        Assert.True(result.Failed);
        Assert.Contains(
            "Upscaler:MemoryBudgetBytes",
            result.FailureMessage,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ValidateResult_SucceedsForValidAndUnsetSettings()
    {
        var validator = new UpscalerConfigValidator();

        Assert.Equal(
            ValidateOptionsResult.Success,
            validator.Validate(
                null,
                new UpscalerConfig { MemoryBudgetBytes = "4GiB", VramSafetyMarginBytes = "512MiB" }
            )
        );
        Assert.Equal(ValidateOptionsResult.Success, validator.Validate(null, new UpscalerConfig()));
    }

    [Fact]
    public void ReportsEveryInvalidSettingAtOnce()
    {
        List<string> invalid = new UpscalerConfig
        {
            MemoryBudgetBytes = "nope",
            VramSafetyMarginBytes = "512 Mi",
            VramExclusiveThresholdBytes = "1 GiBX",
            MaxSpoolBytesPerTask = "eight",
        }
            .FindInvalidSizeSettings()
            .ToList();

        Assert.Equal(4, invalid.Count);
        Assert.Contains(invalid, m => m.Contains("MemoryBudgetBytes", StringComparison.Ordinal));
        Assert.Contains(
            invalid,
            m => m.Contains("VramSafetyMarginBytes", StringComparison.Ordinal)
        );
        Assert.Contains(
            invalid,
            m => m.Contains("VramExclusiveThresholdBytes", StringComparison.Ordinal)
        );
        Assert.Contains(invalid, m => m.Contains("MaxSpoolBytesPerTask", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_IgnoresTheOtherSettings()
    {
        // Only the size settings are validated here; unrelated misconfiguration is not this
        // validator's business and must not fail startup.
        List<string> invalid = new UpscalerConfig
        {
            MemoryBudgetBytes = "4GiB",
            VramUtilizationFraction = 1.5, // clamped elsewhere, out of scope for the size check
            SmartDownscaleFactor = 0.75,
        }
            .FindInvalidSizeSettings()
            .ToList();

        Assert.Empty(invalid);
    }

    [Fact]
    public async Task ValidateOnStart_FailsTheHostForAnInvalidSizeSetting()
    {
        // The real registration path: AddOptions().Bind().ValidateOnStart() plus the validator
        // must abort startup, not log and continue.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:MemoryBudgetBytes"] = "12 Gigabytes",
                }
            )
            .Build();

        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services
                    .AddOptions<UpscalerConfig>()
                    .Bind(configuration.GetSection(UpscalerConfig.Position))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<UpscalerConfig>, UpscalerConfigValidator>();
            })
            .Build();

        OptionsValidationException exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () =>
                host.StartAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("Upscaler:MemoryBudgetBytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("12 Gigabytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnStart_AllowsAValidConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:MemoryBudgetBytes"] = "12 GiB",
                    [$"{UpscalerConfig.Position}:VramExclusiveThresholdBytes"] = "1GiB",
                }
            )
            .Build();

        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
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

        Assert.Equal(12884901888L, resolved.ResolvedMemoryBudgetBytes);
        Assert.Equal(1073741824L, resolved.ResolvedVramExclusiveThresholdBytes);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidateOnStart_AllowsAnEntirelyUnsetConfiguration()
    {
        // "Unset" is the default deployment (Docker without overrides), so it must start.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
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

        Assert.Equal(0, resolved.ResolvedMemoryBudgetBytes);
        Assert.Null(resolved.ResolvedVramSafetyMarginBytes);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Validate_RejectsInvalidPreprocessingSettings()
    {
        // The preprocessing half has to be *wired into* Validate, not merely available: asserting on
        // FindInvalidPreprocessingSettings alone would stay green if the AddRange inside Validate were
        // dropped, and startup would then accept a value ImageResizeService rejects on the first image
        // of the first task instead of at boot.
        var config = new UpscalerConfig { SmartDownscaleFactor = 0.0 };

        Assert.False(new UpscalerConfigValidator().Validate(null, config).Succeeded);
    }

    [Fact]
    public void Validate_RejectsAnUnknownArchitectureName()
    {
        // The engine skips an unrecognised token, so without this check the setting would be silently
        // ignored rather than the boot failing — the same trade-off the size settings make.
        Assert.False(
            new UpscalerConfigValidator()
                .Validate(null, new UpscalerConfig { PreferredModelArchitectures = "FDAT_M,BOGUS" })
                .Succeeded
        );

        // Any accepted spelling passes, and unset means the built-in order.
        Assert.True(
            new UpscalerConfigValidator()
                .Validate(
                    null,
                    new UpscalerConfig { PreferredModelArchitectures = "fdat_xl, FdatM" }
                )
                .Succeeded
        );
        Assert.True(new UpscalerConfigValidator().Validate(null, new UpscalerConfig()).Succeeded);
    }

    [Fact]
    public void OptionsCreate_BypassesValidation()
    {
        // Many call sites construct a config directly; those must not be affected by validation.
        var config = new UpscalerConfig { MemoryBudgetBytes = "nonsense" };
        _ = Options.Create(config);

        // ...while the same value is rejected by the validator that runs at startup. Asserting only
        // that Options.Create did not throw would stay green if the validator were never registered.
        Assert.False(new UpscalerConfigValidator().Validate(null, config).Succeeded);
    }

    [Theory]
    [InlineData(1.5, 15.0, null)] // factor at or above 1
    [InlineData(0.0, 15.0, null)] // factor at or below 0
    [InlineData(0.75, 0.0, null)] // non-positive threshold
    [InlineData(0.75, -1.0, null)] // negative threshold
    [InlineData(0.75, 15.0, -1)] // negative maximum dimension
    public void RejectsInvalidPreprocessingSettings(double factor, double threshold, int? maxDim)
    {
        // EnableSmartDownscale defaults to true, and ImageResizeService rejects these per image. Without
        // a boot-time check the first chapter of the first task fails instead of the boot.
        var config = new UpscalerConfig
        {
            SmartDownscaleFactor = factor,
            SmartDownscaleThreshold = threshold,
            MaxDimensionBeforeUpscaling = maxDim,
        };

        Assert.NotEmpty(config.FindInvalidPreprocessingSettings());
    }

    [Fact]
    public void AcceptsValidPreprocessingSettings()
    {
        var config = new UpscalerConfig
        {
            SmartDownscaleFactor = 0.75,
            SmartDownscaleThreshold = 15.0,
            MaxDimensionBeforeUpscaling = 2048,
        };

        Assert.Empty(config.FindInvalidPreprocessingSettings());
    }

    [Fact]
    public void IgnoresSmartDownscaleSettingsWhileTheFeatureIsDisabled()
    {
        // A nonsense value on a disabled feature is inert, matching ImageResizeService.Validate.
        var config = new UpscalerConfig
        {
            EnableSmartDownscale = false,
            SmartDownscaleFactor = 1.5,
            SmartDownscaleThreshold = 0,
        };

        Assert.Empty(config.FindInvalidPreprocessingSettings());
    }
}
