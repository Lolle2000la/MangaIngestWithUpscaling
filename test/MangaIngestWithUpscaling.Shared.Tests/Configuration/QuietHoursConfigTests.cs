using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// Pins the window arithmetic of <see cref="QuietHoursConfig" />. The type is clock-free on purpose:
/// the processors and the gate supply the local time of day, so the boundaries can be asserted exactly.
/// </summary>
public class QuietHoursConfigTests
{
    private static QuietHoursConfig Daytime() =>
        new()
        {
            Enabled = true,
            Start = TimeSpan.FromHours(9),
            End = TimeSpan.FromHours(17),
        };

    private static QuietHoursConfig Night() =>
        new()
        {
            Enabled = true,
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

    [Fact]
    [Trait("Category", "Unit")]
    public void Disabled_IsNeverActive()
    {
        // The default state must leave every host behaving exactly as it does without the section.
        var config = new QuietHoursConfig
        {
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

        Assert.False(config.Enabled);
        Assert.False(config.IsActive(TimeSpan.FromHours(23)));
        Assert.Null(config.TimeUntilEnd(TimeSpan.FromHours(23)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    [InlineData(21, 0, false)]
    public void DaytimeWindow_CoversStartInclusiveAndEndExclusive(
        int hour,
        int minute,
        bool expected
    )
    {
        // [Start, End): a task starting at exactly End must be allowed, and one at exactly Start
        // must not. An off-by-one here silently shifts the whole window by a minute or an hour.
        QuietHoursConfig config = Daytime();

        Assert.Equal(expected, config.IsActive(new TimeSpan(hour, minute, 0)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(12, 0, false)]
    public void WrappingWindow_CoversTheHoursPastMidnight(int hour, int minute, bool expected)
    {
        // 22:00 -> 06:00 wraps, which is the documented default. Reading it as a plain
        // Start <= t < End range would make the window empty instead.
        QuietHoursConfig config = Night();

        Assert.Equal(expected, config.IsActive(new TimeSpan(hour, minute, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ZeroLengthWindow_IsNeverActive()
    {
        // Start == End is rejected by the validator while the feature is enabled; if it is reached
        // anyway it must not read as "always active" (which would stop the queue forever).
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        Assert.False(config.IsActive(TimeSpan.FromHours(8)));
        Assert.False(config.IsActive(TimeSpan.FromHours(12)));
        Assert.Null(config.TimeUntilEnd(TimeSpan.FromHours(8)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_CountsDownToEndWithinTheSameDay()
    {
        QuietHoursConfig config = Daytime();

        Assert.Equal(TimeSpan.FromHours(5), config.TimeUntilEnd(TimeSpan.FromHours(12)));
        Assert.Equal(TimeSpan.FromHours(8), config.TimeUntilEnd(TimeSpan.FromHours(9)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_CrossesMidnightForAWrappingWindow()
    {
        QuietHoursConfig config = Night();

        // Past the start: the end is on the next day, so 23:30 has 30 minutes plus the 6 hours.
        Assert.Equal(TimeSpan.FromHours(6.5), config.TimeUntilEnd(new TimeSpan(23, 30, 0)));
        // Before the end: the remaining time is still on the same morning.
        Assert.Equal(TimeSpan.FromHours(3), config.TimeUntilEnd(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromHours(6), config.TimeUntilEnd(TimeSpan.Zero));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_IsNullOutsideTheWindow()
    {
        QuietHoursConfig config = Daytime();

        Assert.Null(config.TimeUntilEnd(TimeSpan.FromHours(8)));
        Assert.Null(config.TimeUntilEnd(TimeSpan.FromHours(17)));
    }
}
