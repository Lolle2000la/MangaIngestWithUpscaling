using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// A mistyped schedule must fail the boot rather than silently disable the pause: an out-of-range
/// time of day compares as "outside the window" for every instant, so the feature would look enabled
/// and never fire.
/// </summary>
public class QuietHoursConfigValidatorTests
{
    private static ValidateOptionsResult Validate(QuietHoursConfig config) =>
        new QuietHoursConfigValidator().Validate(null, config);

    /// <summary>The single failure, asserting it is non-null first (Failures is nullable when valid).</summary>
    private static string SingleFailure(ValidateOptionsResult result) =>
        Assert.Single(result.Failures ?? []);

    [Fact]
    [Trait("Category", "Unit")]
    public void DisabledEmptyConfig_IsValid()
    {
        // No schedule at all is the "off" state and must stay legal.
        Assert.True(Validate(new QuietHoursConfig()).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DailyPair_IsValid()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WindowsWithDays_AreValid()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) },
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(12),
                    End = TimeSpan.FromHours(13),
                    Days = [DayOfWeek.Saturday, DayOfWeek.Sunday],
                },
            ],
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OverlappingWindows_AreAllowed()
    {
        // Union semantics: overlap is a legitimate way to express "the union of these ranges".
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) },
                new QuietWindow { Start = TimeSpan.FromHours(3), End = TimeSpan.FromHours(8) },
            ],
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StartWithoutEnd_Fails()
    {
        var config = new QuietHoursConfig { Start = TimeSpan.FromHours(22) };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        Assert.Contains("must be set together", SingleFailure(result), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EndWithoutStart_Fails()
    {
        var config = new QuietHoursConfig { End = TimeSpan.FromHours(6) };

        Assert.Contains(
            "must be set together",
            SingleFailure(Validate(config)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BothShapesTogether_Fail()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(1), End = TimeSpan.FromHours(2) },
            ],
        };

        Assert.Contains("not both", SingleFailure(Validate(config)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnabledWithoutAnyWindow_Fails()
    {
        // Enabled-but-inert is a configuration bug: the operator asked for a pause that can never
        // happen, which is indistinguishable from a typo in the section name.
        var config = new QuietHoursConfig { Enabled = true };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        string failure = SingleFailure(result);
        Assert.Contains("QuietHours:Enabled", failure, StringComparison.Ordinal);
        Assert.Contains("no window", failure, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(30)]
    public void StartOutsideOneDay_Fails(int hours)
    {
        var config = new QuietHoursConfig
        {
            Start = TimeSpan.FromHours(hours),
            End = TimeSpan.FromHours(6),
        };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        Assert.Contains("QuietHours:Start", SingleFailure(result), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-1)]
    [InlineData(24)]
    public void EndOutsideOneDay_Fails(int hours)
    {
        var config = new QuietHoursConfig
        {
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(hours),
        };

        Assert.Contains(
            "QuietHours:End",
            SingleFailure(Validate(config)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutOfRangeValue_FailsEvenWhenDisabled()
    {
        // A typo is a typo whether or not the feature is on today: silently accepting it means
        // enabling the feature later applies a window that can never be active.
        var config = new QuietHoursConfig
        {
            Enabled = false,
            Start = TimeSpan.FromHours(25),
            End = TimeSpan.FromHours(6),
        };

        Assert.True(Validate(config).Failed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutOfRangeWindowEntry_Fails()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(25) },
            ],
        };

        string failure = SingleFailure(Validate(config));
        Assert.Contains("QuietHours:Windows[0].End", failure, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ZeroLengthWindow_Fails()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(8), End = TimeSpan.FromHours(8) },
            ],
        };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        string failure = SingleFailure(result);
        Assert.Contains("zero-length", failure, StringComparison.Ordinal);
        Assert.Contains("QuietHours:Windows[0]", failure, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ZeroLengthDailyPair_Fails()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        Assert.Contains("zero-length", SingleFailure(Validate(config)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ZeroLengthWindow_FailsEvenWhenDisabled()
    {
        // The new rule checks every configured window regardless of Enabled: a zero-length window is
        // never a useful value, so it is treated as a typo rather than as an "off" spelling.
        var config = new QuietHoursConfig
        {
            Enabled = false,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        Assert.True(Validate(config).Failed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidTimeZone_IsAccepted()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            TimeZone = "UTC",
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmptyTimeZone_MeansHostLocalAndIsAccepted()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            TimeZone = "   ",
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        Assert.True(Validate(config).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnknownTimeZone_FailsNamingTheId()
    {
        // Without this check a typo'd zone throws on the first poll inside a background service
        // instead of failing the boot with the offending line in the log.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            TimeZone = "Nowhere/Atlantis",
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        ValidateOptionsResult result = Validate(config);

        Assert.True(result.Failed);
        string failure = SingleFailure(result);
        Assert.Contains("QuietHours:TimeZone", failure, StringComparison.Ordinal);
        Assert.Contains("Nowhere/Atlantis", failure, StringComparison.Ordinal);
    }
}
