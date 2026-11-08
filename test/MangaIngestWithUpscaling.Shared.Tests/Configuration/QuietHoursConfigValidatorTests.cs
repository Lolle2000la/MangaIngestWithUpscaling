using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// A mistyped time of day must fail the boot rather than silently disable the pause: an
/// out-of-range value compares as "outside the window" for every instant, so the feature would look
/// enabled and never fire.
/// </summary>
public class QuietHoursConfigValidatorTests
{
    private static ValidateOptionsResult Validate(QuietHoursConfig config) =>
        new QuietHoursConfigValidator().Validate(null, config);

    [Fact]
    [Trait("Category", "Unit")]
    public void DefaultConfig_IsValid()
    {
        Assert.True(Validate(new QuietHoursConfig()).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnabledWrappingWindow_IsValid()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(30)]
    public void StartOutsideOneDay_Fails(int hours)
    {
        var config = new QuietHoursConfig { Start = TimeSpan.FromHours(hours) };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        Assert.Contains(
            "QuietHours:Start",
            Assert.Single(result.Failures),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-1)]
    [InlineData(24)]
    public void EndOutsideOneDay_Fails(int hours)
    {
        var config = new QuietHoursConfig { End = TimeSpan.FromHours(hours) };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        Assert.Contains("QuietHours:End", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutOfRangeValue_FailsEvenWhenDisabled()
    {
        // A typo is a typo whether or not the feature is on today: silently accepting it means
        // enabling the feature later applies a window that can never be active.
        var config = new QuietHoursConfig { Enabled = false, Start = TimeSpan.FromHours(25) };

        Assert.True(Validate(config).Failed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnabledZeroLengthWindow_Fails()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        string failure = Assert.Single(result.Failures);
        Assert.Contains("zero-length", failure, StringComparison.Ordinal);
        Assert.Contains("QuietHours:Enabled", failure, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisabledZeroLengthWindow_IsValid()
    {
        // Disabled means "off", and Start == End is then inert — the operator is told to disable the
        // feature instead of trying to express "off" with a degenerate window, so that must be valid.
        var config = new QuietHoursConfig
        {
            Enabled = false,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        Assert.True(Validate(config).Succeeded);
    }
}
