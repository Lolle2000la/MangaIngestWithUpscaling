using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.QuietHours;

/// <summary>
///     One evaluation of the quiet-hours schedule, read in one go.
///     <para>
///     Consumers need the flag and the resume time together (a badge, a log line). Reading them as
///     separate properties let a boundary cross between the two reads and produced "paused until "
///     with an empty time for a whole poll interval, so the gate hands out a single value instead.
///     </para>
/// </summary>
/// <param name="IsQuiet">Whether new upscale work must not be started right now.</param>
/// <param name="TimeUntilResume">
///     How long the current quiet period still runs; <see langword="null" /> when it is not quiet, and
///     also when it is quiet with no end in sight (a schedule that covers every hour).
/// </param>
/// <param name="ResumeAtLocal">
///     The wall-clock time of day, in the gate's configured zone, at which the current quiet period
///     ends; <see langword="null" /> when it is not quiet or has no end.
/// </param>
public readonly record struct QuietHoursState(
    bool IsQuiet,
    TimeSpan? TimeUntilResume,
    TimeOnly? ResumeAtLocal
);

/// <summary>
///     Tells a host whether it is currently inside its configured quiet-hours schedule, and lets it
///     wait the schedule out. Shared by both hosts so the server's local upscale processor and each
///     remote worker's fetching loop gate on exactly the same rules.
/// </summary>
public interface IQuietHoursGate
{
    /// <summary>
    ///     The current state of the schedule. Each access is one clock read, so the members of the
    ///     returned value always belong together.
    /// </summary>
    QuietHoursState Current { get; }

    /// <summary>
    ///     Returns immediately when the gate is open; otherwise blocks until the schedule ends,
    ///     re-evaluating the clock as it goes so a wall-clock change cannot strand the caller. A
    ///     schedule with no end in sight never returns (and never spins: it re-checks once a minute).
    /// </summary>
    Task WaitUntilNotQuietAsync(CancellationToken cancellationToken);
}

/// <summary>
///     The <see cref="TimeProvider" />-based <see cref="IQuietHoursGate" />.
///     <para>
///     The schedule is read in <see cref="QuietHoursConfig.TimeZone" /> when one is configured, and in
///     the host's local zone otherwise. The zone is resolved once, in the constructor: the lookup is a
///     filesystem/registry hit that can throw, and the validator has already rejected an unusable id
///     at startup.
///     </para>
/// </summary>
public sealed class QuietHoursGate : IQuietHoursGate
{
    /// <summary>
    ///     Ceiling on a single wait. Re-evaluating at least once a minute means a clock jump (NTP, a
    ///     manual change, a host resumed from suspend, a DST transition) is observed promptly instead
    ///     of after a computed delay that was based on the old clock, and it is also how an endless
    ///     schedule is re-checked without spinning.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Floor on a single wait, so a remainder that rounds to nothing cannot turn the loop into a
    ///     spin that burns a core until the schedule ends.
    /// </summary>
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private static readonly long TicksPerDay = TimeSpan.FromDays(1).Ticks;

    private readonly IOptions<QuietHoursConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _zone;

    public QuietHoursGate(IOptions<QuietHoursConfig> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        string? configured = options.Value.TimeZone;
        _zone = string.IsNullOrWhiteSpace(configured)
            ? timeProvider.LocalTimeZone
            : TimeZoneInfo.FindSystemTimeZoneById(configured);
    }

    private QuietHoursConfig Config => _options.Value;

    /// <summary>
    ///     The wall clock in the configured zone. It comes from <see cref="TimeProvider.GetUtcNow" />
    ///     converted into that zone rather than from the wall-clock APIs, which the UTC guard test
    ///     forbids: the schedule is local, but it still has to be a clock a test can pin.
    /// </summary>
    private DateTime WallClock =>
        TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _zone).DateTime;

    public QuietHoursState Current
    {
        get
        {
            DateTime now = WallClock;
            if (!Config.IsActive(now.DayOfWeek, now.TimeOfDay))
            {
                return default;
            }

            // Null here means the schedule never ends, which is still "quiet" — the flag comes from
            // IsActive above precisely so the two cases cannot be confused.
            TimeSpan? remaining = Config.TimeUntilEnd(now.DayOfWeek, now.TimeOfDay);
            TimeOnly? resumeAt = remaining is TimeSpan left
                // Wrapped into the day so a period ending after midnight reports the morning time.
                ? TimeOnly.FromTimeSpan(
                    TimeSpan.FromTicks((now.TimeOfDay + left).Ticks % TicksPerDay)
                )
                : null;

            return new QuietHoursState(true, remaining, resumeAt);
        }
    }

    public async Task WaitUntilNotQuietAsync(CancellationToken cancellationToken)
    {
        while (Current is { IsQuiet: true } state)
        {
            TimeSpan delay = state.TimeUntilResume ?? MaxWait;
            if (delay > MaxWait)
            {
                delay = MaxWait;
            }
            else if (delay < MinWait)
            {
                delay = MinWait;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }
}
