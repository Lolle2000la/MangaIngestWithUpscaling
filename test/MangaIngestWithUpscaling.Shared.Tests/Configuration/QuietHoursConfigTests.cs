using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// Pins the schedule arithmetic of <see cref="QuietHoursConfig" />. The type is clock-free on purpose:
/// the gate supplies the wall-clock day and time of day, so every boundary can be asserted exactly.
/// </summary>
public class QuietHoursConfigTests
{
    private static QuietHoursConfig Daily() =>
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

    private static readonly DayOfWeek AnyWeekday = DayOfWeek.Wednesday;

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
        Assert.False(config.IsActive(DayOfWeek.Monday, TimeSpan.FromHours(23)));
        Assert.Null(config.TimeUntilEnd(DayOfWeek.Monday, TimeSpan.FromHours(23)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    [InlineData(21, 0, false)]
    public void DailyWindow_CoversStartInclusiveAndEndExclusive(int hour, int minute, bool expected)
    {
        // [Start, End): a task starting at exactly End must be allowed, and one at exactly Start must
        // not. An off-by-one here silently shifts the whole window by a minute or an hour.
        QuietHoursConfig config = Daily();

        Assert.Equal(expected, config.IsActive(AnyWeekday, new TimeSpan(hour, minute, 0)));
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
    public void WrappingWindowWithoutDays_CoversTheHoursPastMidnight(
        int hour,
        int minute,
        bool expected
    )
    {
        // 22:00 -> 06:00 wraps, which is the documented default. Reading it as a plain
        // Start <= t < End range would make the window empty instead.
        QuietHoursConfig config = Night();

        Assert.Equal(expected, config.IsActive(AnyWeekday, new TimeSpan(hour, minute, 0)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    // The window belongs to the day it starts on, so Friday 22:00 runs into Saturday morning only.
    [InlineData(DayOfWeek.Friday, 23, 0, true)]
    [InlineData(DayOfWeek.Saturday, 3, 0, true)]
    [InlineData(DayOfWeek.Saturday, 23, 0, false)]
    [InlineData(DayOfWeek.Sunday, 3, 0, false)]
    [InlineData(DayOfWeek.Friday, 21, 59, false)]
    public void WrappingWindowWithDays_IsOwnedByItsStartDay(
        DayOfWeek day,
        int hour,
        int minute,
        bool expected
    )
    {
        // The subtle half of the schedule: after midnight the clock belongs to the *next* day, so the
        // check must look back one day. Without that a Friday window would not cover Saturday 03:00,
        // and with the look-back applied to the evening hours it would wrongly cover Saturday night.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(22),
                    End = TimeSpan.FromHours(6),
                    Days = [DayOfWeek.Friday],
                },
            ],
        };

        Assert.Equal(expected, config.IsActive(day, new TimeSpan(hour, minute, 0)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    // Same rule with the day on the other side of the week boundary: Sunday's window ends Monday.
    [InlineData(DayOfWeek.Sunday, 23, 0, true)]
    [InlineData(DayOfWeek.Monday, 3, 0, true)]
    [InlineData(DayOfWeek.Monday, 23, 0, false)]
    [InlineData(DayOfWeek.Sunday, 3, 0, false)]
    public void WrappingWindowWithDays_HandlesTheWeekBoundary(
        DayOfWeek day,
        int hour,
        int minute,
        bool expected
    )
    {
        // Saturday -> Sunday is 6 -> 0, so the previous-day arithmetic has to wrap the enum too.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(22),
                    End = TimeSpan.FromHours(6),
                    Days = [DayOfWeek.Sunday],
                },
            ],
        };

        Assert.Equal(expected, config.IsActive(day, new TimeSpan(hour, minute, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmptyDays_MeansEveryDay()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) },
            ],
        };

        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            Assert.True(config.IsActive(day, TimeSpan.FromHours(23)), $"{day} evening");
            Assert.True(config.IsActive(day, TimeSpan.FromHours(3)), $"{day} morning");
            Assert.False(config.IsActive(day, TimeSpan.FromHours(12)), $"{day} midday");
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MultipleWindows_CombineAsAUnion()
    {
        // A daily night window plus a weekend lunch break: any match is quiet, and the weekday
        // restriction applies to its own window only.
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

        Assert.True(config.IsActive(DayOfWeek.Saturday, new TimeSpan(12, 30, 0)));
        Assert.False(config.IsActive(DayOfWeek.Friday, new TimeSpan(12, 30, 0)));
        Assert.True(config.IsActive(DayOfWeek.Friday, TimeSpan.FromHours(23)));
        Assert.False(config.IsActive(DayOfWeek.Friday, new TimeSpan(15, 0, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NonWrappingWindowWithDays_OnlyCoversThoseDays()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(9),
                    End = TimeSpan.FromHours(17),
                    Days = [DayOfWeek.Friday],
                },
            ],
        };

        Assert.True(config.IsActive(DayOfWeek.Friday, TimeSpan.FromHours(10)));
        Assert.False(config.IsActive(DayOfWeek.Thursday, TimeSpan.FromHours(10)));
        // No look-back for a non-wrapping window: Saturday morning is not Friday's window.
        Assert.False(config.IsActive(DayOfWeek.Saturday, TimeSpan.FromHours(10)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EffectiveWindows_NormaliseTheDailyPairToOneAllDaysWindow()
    {
        // The simple form must run through the same code path as an explicit list, not a second
        // implementation that can drift from it.
        QuietHoursConfig config = Night();

        QuietWindow window = Assert.Single(config.EffectiveWindows());
        Assert.Equal(TimeSpan.FromHours(22), window.Start);
        Assert.Equal(TimeSpan.FromHours(6), window.End);
        Assert.Empty(window.Days);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EffectiveWindows_PreferTheExplicitList()
    {
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(1),
                    End = TimeSpan.FromHours(2),
                    Days = [DayOfWeek.Monday],
                },
            ],
        };

        QuietWindow window = Assert.Single(config.EffectiveWindows());
        Assert.Equal(TimeSpan.FromHours(1), window.Start);
        Assert.Equal(TimeSpan.FromHours(2), window.End);
        Assert.Equal([DayOfWeek.Monday], window.Days);
        // The daily pair is not consulted at all while an explicit list exists.
        Assert.False(config.IsActive(DayOfWeek.Tuesday, new TimeSpan(1, 30, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ZeroLengthWindow_IsNeverActive()
    {
        // Start == End is rejected by the validator; if it is reached anyway it must not read as
        // "always active" (which would stop the queue forever).
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Start = TimeSpan.FromHours(8),
            End = TimeSpan.FromHours(8),
        };

        Assert.False(config.IsActive(DayOfWeek.Monday, TimeSpan.FromHours(8)));
        Assert.False(config.IsActive(DayOfWeek.Monday, TimeSpan.FromHours(12)));
        Assert.Null(config.TimeUntilEnd(DayOfWeek.Monday, TimeSpan.FromHours(8)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_CountsDownToEndWithinTheSameDay()
    {
        QuietHoursConfig config = Daily();

        Assert.Equal(
            TimeSpan.FromHours(5),
            config.TimeUntilEnd(AnyWeekday, TimeSpan.FromHours(12))
        );
        Assert.Equal(TimeSpan.FromHours(8), config.TimeUntilEnd(AnyWeekday, TimeSpan.FromHours(9)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_CrossesMidnightForAWrappingWindow()
    {
        QuietHoursConfig config = Night();

        // Past the start: the end is on the next day, so 23:30 has 30 minutes plus the 6 hours.
        Assert.Equal(
            TimeSpan.FromHours(6.5),
            config.TimeUntilEnd(DayOfWeek.Monday, new TimeSpan(23, 30, 0))
        );
        // Before the end: the remaining time is still on the same morning.
        Assert.Equal(
            TimeSpan.FromHours(3),
            config.TimeUntilEnd(DayOfWeek.Tuesday, TimeSpan.FromHours(3))
        );
        Assert.Equal(TimeSpan.FromHours(6), config.TimeUntilEnd(DayOfWeek.Tuesday, TimeSpan.Zero));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_ReturnsTheNextBoundaryAcrossSeveralWindows()
    {
        // 12:30 on Saturday matches both the weekend lunch window (0.5 h left) and the night window
        // (no match yet), so the next boundary — and the value the gate sleeps on — is the lunch one.
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
                    Days = [DayOfWeek.Saturday],
                },
            ],
        };

        Assert.Equal(
            TimeSpan.FromMinutes(30),
            config.TimeUntilEnd(DayOfWeek.Saturday, new TimeSpan(12, 30, 0))
        );
        Assert.Equal(
            TimeSpan.FromHours(6.5),
            config.TimeUntilEnd(DayOfWeek.Saturday, new TimeSpan(23, 30, 0))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_ReportsTheEndOfTheMergedPeriodForOverlappingWindows()
    {
        // 22:00-06:00 plus 05:00-07:00: at 05:30 the pause really runs to 07:00, so reporting the
        // next boundary (06:00) would understate it to both the log and the UI badge.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) },
                new QuietWindow { Start = TimeSpan.FromHours(5), End = TimeSpan.FromHours(7) },
            ],
        };

        Assert.Equal(
            TimeSpan.FromHours(1.5),
            config.TimeUntilEnd(DayOfWeek.Monday, new TimeSpan(5, 30, 0))
        );
        // The overlap also extends the period from the evening side: 23:00 is quiet until 07:00.
        Assert.Equal(
            TimeSpan.FromHours(8),
            config.TimeUntilEnd(DayOfWeek.Monday, TimeSpan.FromHours(23))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_MergesWindowsThatMerelyTouch()
    {
        // 22:00-06:00 followed by 06:00-08:00 is one pause from 22:00 to 08:00: a pause that ends at
        // a boundary another window opens on is a single period from the operator's point of view.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) },
                new QuietWindow { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(8) },
            ],
        };

        Assert.Equal(
            TimeSpan.FromHours(9),
            config.TimeUntilEnd(DayOfWeek.Monday, TimeSpan.FromHours(23))
        );
        Assert.Equal(
            TimeSpan.FromHours(7),
            config.TimeUntilEnd(DayOfWeek.Tuesday, TimeSpan.FromHours(1))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AllDaySchedule_IsActiveEveryHourAndHasNoEnd()
    {
        // 00:00-12:00 plus 12:00-00:00 covers every instant. Reporting a finite end would name a
        // resume that never arrives (the two windows keep handing over to each other), so the answer
        // is "quiet, with no end", not a boundary ~a day and a half out.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.Zero, End = TimeSpan.FromHours(12) },
                new QuietWindow { Start = TimeSpan.FromHours(12), End = TimeSpan.Zero },
            ],
        };

        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            for (int minutes = 0; minutes < 24 * 60; minutes += 30)
            {
                TimeSpan timeOfDay = TimeSpan.FromMinutes(minutes);
                Assert.True(config.IsActive(day, timeOfDay), $"{day} {timeOfDay}");
                Assert.Null(config.TimeUntilEnd(day, timeOfDay));
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NormalWindow_StillReportsItsEnd()
    {
        // The counterpart to the all-day case: a schedule that stops must keep naming when.
        QuietHoursConfig config = Night();

        Assert.Equal(
            TimeSpan.FromHours(6.5),
            config.TimeUntilEnd(DayOfWeek.Monday, new TimeSpan(23, 30, 0))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsActive_And_TimeUntilEnd_AgreeEverywhereInTheWeek()
    {
        // The two answers come from different code paths — a direct membership test and a projected,
        // merged interval — so a schedule with weekday restrictions and a wrap must keep them equal.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(22),
                    End = TimeSpan.FromHours(6),
                    Days = [DayOfWeek.Friday, DayOfWeek.Saturday],
                },
                new QuietWindow
                {
                    Start = TimeSpan.FromHours(5),
                    End = TimeSpan.FromHours(7),
                    Days = [DayOfWeek.Saturday],
                },
                new QuietWindow { Start = TimeSpan.FromHours(12), End = TimeSpan.FromHours(13) },
            ],
        };

        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            for (int minutes = 0; minutes < 24 * 60; minutes += 15)
            {
                TimeSpan timeOfDay = TimeSpan.FromMinutes(minutes);
                bool active = config.IsActive(day, timeOfDay);
                TimeSpan? remaining = config.TimeUntilEnd(day, timeOfDay);

                // The end may be null while the schedule is quiet (a schedule that never ends), so the
                // invariant is one-directional: a reported end proves the instant is quiet, and an
                // open schedule never reports one.
                if (!active)
                {
                    Assert.Null(remaining);
                }

                if (remaining is TimeSpan left)
                {
                    Assert.True(active, $"{day} {timeOfDay}: an end was reported while open");
                    Assert.True(left > TimeSpan.Zero, $"{day} {timeOfDay}: non-positive remaining");
                    // The reported end must itself be a moment the schedule stops covering.
                    long dayTicks = TimeSpan.FromDays(1).Ticks;
                    TimeSpan endsAt = timeOfDay + left;
                    DayOfWeek endDay = day;
                    for (long i = 0; i < endsAt.Ticks / dayTicks; i++)
                    {
                        endDay = NextDay(endDay);
                    }

                    Assert.False(
                        config.IsActive(endDay, TimeSpan.FromTicks(endsAt.Ticks % dayTicks)),
                        $"{day} {timeOfDay}: quiet supposed to resume at {endsAt}, still quiet"
                    );
                }
            }
        }
    }

    private static DayOfWeek NextDay(DayOfWeek day) => (DayOfWeek)(((int)day + 1) % 7);

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeUntilEnd_IsNullOutsideTheWindow()
    {
        QuietHoursConfig config = Daily();

        Assert.Null(config.TimeUntilEnd(AnyWeekday, TimeSpan.FromHours(8)));
        Assert.Null(config.TimeUntilEnd(AnyWeekday, TimeSpan.FromHours(17)));
    }
}
