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

        Assert.True(gate.IsQuiet);
        // 23:00 local -> 06:00 next day is 7 hours away.
        Assert.Equal(TimeSpan.FromHours(7), gate.TimeUntilResume);
        Assert.Equal(new TimeOnly(6, 0), gate.ResumeAtLocal);
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

        Assert.True(gate.IsQuiet);
        Assert.Equal(new TimeOnly(6, 0), gate.ResumeAtLocal);
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

        Assert.False(gate.IsQuiet);
        Assert.Null(gate.TimeUntilResume);
        Assert.Null(gate.ResumeAtLocal);
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

        Assert.False(gate.IsQuiet);
        Assert.Null(gate.TimeUntilResume);
        Assert.Null(gate.ResumeAtLocal);
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

        Assert.False(gate.IsQuiet);
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

        Assert.True(gate.IsQuiet);
        Assert.Equal(new TimeOnly(13, 0), gate.ResumeAtLocal);
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
        Assert.False(gate.IsQuiet);
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
