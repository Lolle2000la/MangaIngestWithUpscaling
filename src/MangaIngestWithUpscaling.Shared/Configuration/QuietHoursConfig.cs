namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
///     A local-wall-clock window during which a host stops <em>starting</em> new upscaling work.
///     <para>
///     Only the upscaling pipeline pauses: ingest, analysis and the rest of the standard background
///     queue keep running. The window is interpreted in the host's local time zone and applies to two
///     independent halves of the system: the main app's local upscale processor and each remote
///     worker's fetching loop. It never interrupts an upscale that is already claimed or running, and
///     it never closes the server's gRPC task-serving path, so remote workers keep fetching while the
///     server's local upscale queue is asleep. See <c>docs/QUIET_HOURS.md</c>.
///     </para>
///     <para>
///     <see cref="TimeSpan" /> rather than <see cref="TimeOnly" /> is used for the two boundaries
///     because <see cref="TimeSpan" /> is already proven to bind in both hosts, including through the
///     remote worker's configuration source generator (<see cref="UpscalerConfig.ManifestTimeout" />).
///     </para>
/// </summary>
public record QuietHoursConfig
{
    public const string Position = "QuietHours";

    /// <summary>
    ///     Whether the window is applied at all. Disabled by default: with it off, every processor
    ///     behaves exactly as it does without this section.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Start of the window, inclusive, as a time of day. Defaults to 22:00.
    /// </summary>
    public TimeSpan Start { get; set; } = TimeSpan.FromHours(22);

    /// <summary>
    ///     End of the window, exclusive, as a time of day. Defaults to 06:00. A value earlier than
    ///     <see cref="Start" /> makes the window wrap past midnight.
    /// </summary>
    public TimeSpan End { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     Whether <paramref name="timeOfDay" /> falls inside <c>[Start, End)</c>, the window wrapping
    ///     past midnight when <see cref="End" /> is earlier than <see cref="Start" />.
    ///     <para>
    ///     A zero-length window (<see cref="Start" /> == <see cref="End" />) is treated as inactive
    ///     rather than as either "always" or "never": the validator rejects that combination while
    ///     the feature is enabled, so this only guards a defensive configuration.
    ///     </para>
    /// </summary>
    public bool IsActive(TimeSpan timeOfDay)
    {
        if (!Enabled || Start == End)
        {
            return false;
        }

        return Start < End
            ? timeOfDay >= Start && timeOfDay < End
            : timeOfDay >= Start || timeOfDay < End;
    }

    /// <summary>
    ///     How long the window still runs from <paramref name="timeOfDay" />, or <see langword="null" />
    ///     when it is not active. Never negative and never longer than a day, so a caller can use it
    ///     directly as a delay.
    /// </summary>
    public TimeSpan? TimeUntilEnd(TimeSpan timeOfDay)
    {
        if (!IsActive(timeOfDay))
        {
            return null;
        }

        if (End > Start)
        {
            return End - timeOfDay;
        }

        // Wrapping window: past Start the end is on the next day, before End it is the same morning.
        return timeOfDay >= Start ? TimeSpan.FromDays(1) - timeOfDay + End : End - timeOfDay;
    }
}
