using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.QuietHours;

/// <summary>
/// Pins the gate's wall clock: the schedule is read in the configured
/// <see cref="QuietHoursConfig.TimeZone" /> when one is set and in the host's local zone otherwise,
/// never in UTC by accident. The clock is a hand-written <see cref="TimeProvider" /> (no test-time
/// package is used) so the instant, the host zone and the configured zone are all exact.
/// </summary>
public class QuietHoursGateTests
{
    private static readonly TimeZoneInfo UtcPlusTwo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC+02",
        TimeSpan.FromHours(2),
        "Test/UTC+02",
        "Test/UTC+02"
    );

    private static readonly TimeZoneInfo UtcMinusFive = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC-05",
        TimeSpan.FromHours(-5),
        "Test/UTC-05",
        "Test/UTC-05"
    );

    private static QuietHoursConfig Night() =>
        new()
        {
            Enabled = true,
            Start = TimeSpan.FromHours(22),
            End = TimeSpan.FromHours(6),
        };

    private static QuietHoursGate CreateGate(
        QuietHoursConfig config,
        DateTimeOffset utcNow,
        TimeZoneInfo? localTimeZone = null
    ) =>
        new(
            Options.Create(config),
            new FixedTimeProvider(utcNow, localTimeZone ?? TimeZoneInfo.Utc)
        );

    [Fact]
    [Trait("Category", "Unit")]
    public void IsQuiet_UsesTheHostZoneByDefault()
    {
        // 21:00 UTC is 23:00 in UTC+2, so the 22:00-06:00 window is open locally although a UTC
        // reading would put the instant an hour before the window starts.
        QuietHoursGate gate = CreateGate(
            Night(),
            new DateTimeOffset(2026, 1, 5, 21, 0, 0, TimeSpan.Zero),
            UtcPlusTwo
        );

        Assert.True(gate.Current.IsQuiet);
        // 23:00 local -> 06:00 next day is 7 hours away.
        Assert.Equal(TimeSpan.FromHours(7), gate.Current.TimeUntilResume);
        Assert.Equal(new TimeOnly(6, 0), gate.Current.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeZoneOverride_ShiftsTheWallClockAwayFromTheHostZone()
    {
        // 17:00 UTC is 12:00 in the host zone (UTC-5) and 02:00 in Asia/Tokyo (UTC+9). The host zone
        // says "not quiet"; the configured zone says "inside the window". Only honouring TimeZone can
        // produce IsQuiet — this is the container-TZ=UTC trap in miniature.
        var config = Night();
        config.TimeZone = "Asia/Tokyo";
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero),
            UtcMinusFive
        );

        Assert.True(gate.Current.IsQuiet);
        Assert.Equal(new TimeOnly(6, 0), gate.Current.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeZoneOverride_CanAlsoCloseTheWindowTheHostZoneWouldOpen()
    {
        // 13:00 UTC is 08:00 in the host zone (UTC-5, inside a 06:00-09:00 window) but 22:00 in
        // Tokyo — outside a 06:00-09:00 Tokyo window. If the override is ignored the gate reports
        // quiet here, so the assertion fails loudly rather than silently.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            TimeZone = "Asia/Tokyo",
            Start = TimeSpan.FromHours(6),
            End = TimeSpan.FromHours(9),
        };
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 5, 13, 0, 0, TimeSpan.Zero),
            UtcMinusFive
        );

        Assert.False(gate.Current.IsQuiet);
        Assert.Null(gate.Current.TimeUntilResume);
        Assert.Null(gate.Current.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsQuiet_IsFalseOutsideTheWindow()
    {
        // 10:00 UTC is 12:00 in UTC+2, the middle of the day.
        QuietHoursGate gate = CreateGate(
            Night(),
            new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero),
            UtcPlusTwo
        );

        Assert.False(gate.Current.IsQuiet);
        Assert.Null(gate.Current.TimeUntilResume);
        Assert.Null(gate.Current.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsQuiet_IsFalseWhenTheFeatureIsDisabled()
    {
        // 23:00 UTC+2 would be inside the window, but a disabled schedule must never pause anything.
        QuietHoursConfig config = Night();
        config.Enabled = false;
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 5, 21, 0, 0, TimeSpan.Zero),
            UtcPlusTwo
        );

        Assert.False(gate.Current.IsQuiet);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResumeAtLocal_ReportsTheNextBoundaryOfTheMatchingWindow()
    {
        // 12:30 Saturday matches a weekend lunch window that ends at 13:00, while the daily night
        // window ends at 06:00 — the resume time must be the next boundary, 13:00.
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
        // 2026-01-10 is a Saturday; 12:30 UTC, UTC zone.
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 10, 12, 30, 0, TimeSpan.Zero)
        );

        Assert.True(gate.Current.IsQuiet);
        Assert.Equal(new TimeOnly(13, 0), gate.Current.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitUntilNotQuietAsync_ReturnsImmediatelyWhenTheGateIsOpen()
    {
        QuietHoursGate gate = CreateGate(
            Night(),
            new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero),
            UtcPlusTwo
        );

        Task wait = gate.WaitUntilNotQuietAsync(TestContext.Current.CancellationToken);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitUntilNotQuietAsync_KeepsWaitingWhileTheScheduleIsOpen()
    {
        // A gate that returned immediately would let the host start new work inside the window, which
        // is the whole defect this service exists to prevent. The clock does not advance here, so the
        // only way out of the wait is the cancellation.
        QuietHoursGate gate = CreateGate(
            Night(),
            new DateTimeOffset(2026, 1, 5, 21, 0, 0, TimeSpan.Zero),
            UtcPlusTwo
        );

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.WaitUntilNotQuietAsync(cancellation.Token)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitUntilNotQuietAsync_ReturnsOnceTheClockLeavesTheWindow()
    {
        // The wait must re-evaluate the clock rather than sleep for the whole remaining window: it
        // caps each delay at a minute, so a clock that jumps past End has to be observed.
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2026, 1, 5, 5, 59, 59, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        var gate = new QuietHoursGate(Options.Create(Night()), clock);

        Task wait = gate.WaitUntilNotQuietAsync(TestContext.Current.CancellationToken);

        Assert.False(wait.IsCompleted);
        // One second later the window has ended (End is exclusive at 06:00).
        clock.UtcNow = new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(gate.Current.IsQuiet);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitUntilNotQuietAsync_DoesNotOversleepWhenTheBoundaryFallsBetweenTwoReads()
    {
        // Regression guard: the quiet flag and the remaining time used to be read separately, so a
        // clock that crossed the window end between the two reads produced "quiet" followed by
        // "no remaining time" and the loop fell back to a full-minute sleep. One snapshot per
        // iteration makes that state impossible; the wait must return as soon as the clock says the
        // schedule is open.
        // Two reads report the last second inside the window; from the third on the window has closed.
        // Reading the flag and the remaining time separately spends exactly those reads before it has
        // to decide how long to sleep.
        var clock = new BoundaryCrossingClock(
            quietUntil: new DateTimeOffset(2026, 1, 5, 5, 59, 59, TimeSpan.Zero),
            afterwards: new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero),
            quietReads: 2
        );
        var gate = new QuietHoursGate(Options.Create(Night()), clock);

        await gate.WaitUntilNotQuietAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(gate.Current.IsQuiet);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AllDaySchedule_IsQuietWithNoResumeTimeToReport()
    {
        // An endless schedule is still quiet, but it has no resume time: the badge and the log must
        // not name one.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.Zero, End = TimeSpan.FromHours(12) },
                new QuietWindow { Start = TimeSpan.FromHours(12), End = TimeSpan.Zero },
            ],
        };
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero)
        );

        QuietHoursState state = gate.Current;

        Assert.True(state.IsQuiet);
        Assert.Null(state.TimeUntilResume);
        Assert.Null(state.ResumeAtLocal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitUntilNotQuietAsync_KeepsReCheckingAnEndlessScheduleWithoutSpinning()
    {
        // With no end to sleep to, the loop re-checks once a minute: it must not return early (the
        // schedule is still quiet) and must not spin.
        var config = new QuietHoursConfig
        {
            Enabled = true,
            Windows =
            [
                new QuietWindow { Start = TimeSpan.Zero, End = TimeSpan.FromHours(12) },
                new QuietWindow { Start = TimeSpan.FromHours(12), End = TimeSpan.Zero },
            ],
        };
        QuietHoursGate gate = CreateGate(
            config,
            new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero)
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.WaitUntilNotQuietAsync(cancellation.Token)
        );
    }

    /// <summary>
    /// Answers the first <see cref="TimeProvider.GetUtcNow" /> call with the last instant inside the
    /// window and every later one with the first instant outside it — the boundary arriving while the
    /// gate is deciding what to do.
    /// </summary>
    private sealed class BoundaryCrossingClock(
        DateTimeOffset quietUntil,
        DateTimeOffset afterwards,
        int quietReads
    ) : TimeProvider
    {
        private int _reads;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) <= quietReads ? quietUntil : afterwards;
    }

    /// <summary>
    /// A <see cref="TimeProvider" /> with an exact, settable UTC instant and an exact time zone.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
        : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
