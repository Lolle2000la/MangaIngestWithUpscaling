using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.QuietHours;

/// <summary>
///     Tells a host whether it is currently inside its configured quiet-hours schedule, and lets it
///     wait the schedule out. Shared by both hosts so the server's local upscale processor and each
///     remote worker's fetching loop gate on exactly the same rules.
/// </summary>
public interface IQuietHoursGate
{
    /// <summary>Whether new upscale work must not be started right now.</summary>
    bool IsQuiet { get; }

    /// <summary>
    ///     How long the current quiet period still runs, or <see langword="null" /> when it is not
    ///     quiet.
    /// </summary>
    TimeSpan? TimeUntilResume { get; }

    /// <summary>
    ///     The wall-clock time of day, in the gate's configured time zone, at which the current quiet
    ///     period ends; <see langword="null" /> when it is not quiet. Lets a UI show when work resumes
    ///     without reading a clock itself.
    /// </summary>
    TimeOnly? ResumeAtLocal { get; }

    /// <summary>
    ///     Returns immediately when the gate is open; otherwise blocks until the schedule ends,
    ///     re-evaluating the clock as it goes so a wall-clock change cannot strand the caller.
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
    ///     of after a computed delay that was based on the old clock.
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

    public bool IsQuiet => Config.IsActive(WallClock.DayOfWeek, WallClock.TimeOfDay);

    public TimeSpan? TimeUntilResume => Remaining(WallClock);

    public TimeOnly? ResumeAtLocal
    {
        get
        {
            DateTime now = WallClock;
            TimeSpan? remaining = Remaining(now);
            if (remaining is null)
            {
                return null;
            }

            // Wrapped into the day so a window ending after midnight reports the morning time.
            long resumeTicks = (now.TimeOfDay + remaining.Value).Ticks % TicksPerDay;
            return TimeOnly.FromTimeSpan(TimeSpan.FromTicks(resumeTicks));
        }
    }

    public async Task WaitUntilNotQuietAsync(CancellationToken cancellationToken)
    {
        while (IsQuiet)
        {
            TimeSpan remaining = TimeUntilResume ?? MaxWait;
            TimeSpan delay = remaining > MaxWait ? MaxWait : remaining;
            if (delay < MinWait)
            {
                delay = MinWait;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    private TimeSpan? Remaining(DateTime now) => Config.TimeUntilEnd(now.DayOfWeek, now.TimeOfDay);
}
