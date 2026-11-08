namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
///     One quiet-hours window: a time-of-day range that may be restricted to specific weekdays.
/// </summary>
public record QuietWindow
{
    /// <summary>
    ///     Start of the window, inclusive, as a local time of day.
    /// </summary>
    public TimeSpan Start { get; set; }

    /// <summary>
    ///     End of the window, exclusive, as a local time of day. A value earlier than
    ///     <see cref="Start" /> makes the window wrap past midnight, in which case it belongs to the
    ///     weekday it <em>starts</em> on.
    /// </summary>
    public TimeSpan End { get; set; }

    /// <summary>
    ///     Weekdays the window opens on. Empty (the default) means every day. For a window that wraps
    ///     past midnight the day is the one the window starts on, so
    ///     <c>22:00-06:00 { "Days": ["Friday"] }</c> covers Friday 22:00 through Saturday 06:00.
    /// </summary>
    public List<DayOfWeek> Days { get; set; } = [];
}

/// <summary>
///     A local-wall-clock schedule during which a host stops <em>starting</em> new upscaling work.
///     <para>
///     Only the upscaling pipeline pauses: ingest, analysis and the rest of the standard background
///     queue keep running. The schedule is interpreted in the host's local time zone (or the
///     configured <see cref="TimeZone" />) and applies to two independent halves of the system: the
///     main app's local upscale processor and each remote worker's fetching loop. It never interrupts
///     an upscale that is already claimed or running, and it never closes the server's gRPC
///     task-serving path, so remote workers keep fetching while the server's local upscale queue is
///     asleep. See <c>docs/QUIET_HOURS.md</c>.
///     </para>
///     <para>
///     Two shapes are accepted, and both run through <see cref="EffectiveWindows" />: the daily
///     <see cref="Start" /> / <see cref="End" /> pair, or an explicit <see cref="Windows" /> list.
///     </para>
///     <para>
///     <see cref="TimeSpan" /> rather than <see cref="TimeOnly" /> is used for the boundaries because
///     <see cref="TimeSpan" /> is already proven to bind in both hosts, including through the remote
///     worker's configuration source generator (<see cref="UpscalerConfig.ManifestTimeout" />).
///     </para>
/// </summary>
public record QuietHoursConfig
{
    public const string Position = "QuietHours";

    /// <summary>
    ///     Whether the schedule is applied at all. Disabled by default: with it off, every processor
    ///     behaves exactly as it does without this section.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Optional IANA or Windows time zone id (for example <c>Europe/Berlin</c>) the wall clock is
    ///     read in. Unset means the host's own local zone.
    ///     <para>
    ///     This exists because containers default to <c>TZ=UTC</c>: without it a <c>22:00-06:00</c>
    ///     window silently becomes 22:00-06:00 UTC, which is the wrong local night for most
    ///     operators.
    ///     </para>
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    ///     Start of the daily window, inclusive. Must be set together with <see cref="End" />, and
    ///     cannot be combined with <see cref="Windows" />.
    /// </summary>
    public TimeSpan? Start { get; set; }

    /// <summary>
    ///     End of the daily window, exclusive. A value earlier than <see cref="Start" /> makes the
    ///     window wrap past midnight.
    /// </summary>
    public TimeSpan? End { get; set; }

    /// <summary>
    ///     Explicit windows, each optionally restricted to weekdays. Empty means the daily
    ///     <see cref="Start" /> / <see cref="End" /> pair applies instead. Overlapping windows are
    ///     allowed and combine as a union.
    /// </summary>
    public List<QuietWindow> Windows { get; set; } = [];

    /// <summary>
    ///     The windows actually in force: <see cref="Windows" /> when it is non-empty, otherwise the
    ///     daily <see cref="Start" /> / <see cref="End" /> pair normalised to a single all-days window.
    ///     Empty when neither shape is configured.
    /// </summary>
    public IReadOnlyList<QuietWindow> EffectiveWindows()
    {
        if (Windows is { Count: > 0 })
        {
            return Windows;
        }

        return Start is TimeSpan start && End is TimeSpan end
            ? [new QuietWindow { Start = start, End = end }]
            : [];
    }

    /// <summary>
    ///     Whether <paramref name="timeOfDay" /> on <paramref name="day" /> falls inside any effective
    ///     window, as <c>[Start, End)</c>.
    ///     <para>
    ///     A window whose <see cref="QuietWindow.End" /> is earlier than its
    ///     <see cref="QuietWindow.Start" /> wraps past midnight and belongs to the day it starts on:
    ///     the hours after <c>Start</c> are matched against <paramref name="day" />, and the hours
    ///     before <c>End</c> against the previous day. A zero-length window is treated as inactive
    ///     (the validator rejects it) rather than as either "always" or "never".
    ///     </para>
    /// </summary>
    public bool IsActive(DayOfWeek day, TimeSpan timeOfDay)
    {
        if (!Enabled)
        {
            return false;
        }

        foreach (QuietWindow window in EffectiveWindows())
        {
            if (IsWindowActive(window, day, timeOfDay))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     How long the quiet period still runs from <paramref name="timeOfDay" /> on
    ///     <paramref name="day" />, or <see langword="null" /> when it is not active.
    ///     <para>
    ///     The value is the end of the <em>contiguous</em> quiet period containing that instant, not
    ///     the next window boundary: with <c>22:00-06:00</c> plus <c>05:00-07:00</c>, 05:30 is quiet
    ///     until 07:00, and reporting 06:00 would understate the pause to the log and to the UI
    ///     badge. Windows that overlap or merely touch therefore merge.
    ///     </para>
    ///     <para>
    ///     The calculation is clock-free: every window is projected onto the local timeline for the
    ///     days around <paramref name="day" />, the intervals are merged, and the end of the one
    ///     containing <paramref name="timeOfDay" /> is returned. Because schedules can chain across
    ///     days, the result is not capped at one day; callers that sleep on it re-check at least every
    ///     minute anyway.
    ///     </para>
    /// </summary>
    public TimeSpan? TimeUntilEnd(DayOfWeek day, TimeSpan timeOfDay)
    {
        if (!Enabled)
        {
            return null;
        }

        TimeSpan? end = ContiguousEnd(day, timeOfDay);
        return end is TimeSpan quietEnd ? quietEnd - timeOfDay : null;
    }

    /// <summary>
    ///     End of the merged quiet interval containing <paramref name="timeOfDay" />, or
    ///     <see langword="null" /> when that instant is not quiet.
    /// </summary>
    private TimeSpan? ContiguousEnd(DayOfWeek day, TimeSpan timeOfDay)
    {
        TimeSpan dayLength = TimeSpan.FromDays(1);
        List<(TimeSpan Start, TimeSpan End)> intervals = [];

        // Yesterday, today and tomorrow cover every window that can reach the instant under test: the
        // tail of one that began the previous evening, today's, and one that opens just after midnight
        // tomorrow and continues a period that started today.
        foreach (QuietWindow window in EffectiveWindows())
        {
            if (window.Start == window.End)
            {
                continue;
            }

            for (int offset = -1; offset <= 1; offset++)
            {
                if (!AllowsDay(window, OffsetDay(day, offset)))
                {
                    continue;
                }

                TimeSpan start = dayLength * offset + window.Start;
                // A wrapping window ends on the following day, which is what makes its evening and
                // morning halves a single interval rather than two.
                TimeSpan end =
                    window.End > window.Start
                        ? dayLength * offset + window.End
                        : dayLength * (offset + 1) + window.End;
                intervals.Add((start, end));
            }
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        bool haveInterval = false;
        TimeSpan mergedStart = TimeSpan.Zero;
        TimeSpan mergedEnd = TimeSpan.Zero;
        foreach ((TimeSpan start, TimeSpan end) in intervals)
        {
            if (!haveInterval)
            {
                mergedStart = start;
                mergedEnd = end;
                haveInterval = true;
                continue;
            }

            // Overlapping or merely touching windows are one quiet period: a pause that ends at 06:00
            // and another that opens at 06:00 is a single pause from the operator's point of view.
            if (start <= mergedEnd)
            {
                if (end > mergedEnd)
                {
                    mergedEnd = end;
                }

                continue;
            }

            if (mergedStart <= timeOfDay && timeOfDay < mergedEnd)
            {
                return mergedEnd;
            }

            mergedStart = start;
            mergedEnd = end;
        }

        return haveInterval && mergedStart <= timeOfDay && timeOfDay < mergedEnd ? mergedEnd : null;
    }

    private static bool IsWindowActive(QuietWindow window, DayOfWeek day, TimeSpan timeOfDay)
    {
        if (window.Start == window.End)
        {
            return false;
        }

        if (window.Start < window.End)
        {
            return AllowsDay(window, day) && timeOfDay >= window.Start && timeOfDay < window.End;
        }

        // Wrapping window, owned by its start day: the evening half is this day, the morning half is
        // the tail of the previous day's window.
        return (AllowsDay(window, day) && timeOfDay >= window.Start)
            || (AllowsDay(window, PreviousDay(day)) && timeOfDay < window.End);
    }

    /// <summary>An empty day list means every day.</summary>
    private static bool AllowsDay(QuietWindow window, DayOfWeek day) =>
        window.Days is not { Count: > 0 } || window.Days.Contains(day);

    private static DayOfWeek PreviousDay(DayOfWeek day) => OffsetDay(day, -1);

    /// <summary>Shifts a weekday by <paramref name="days" />, wrapping around the week in both directions.</summary>
    private static DayOfWeek OffsetDay(DayOfWeek day, int days) =>
        (DayOfWeek)((((int)day + days) % 7 + 7) % 7);
}
