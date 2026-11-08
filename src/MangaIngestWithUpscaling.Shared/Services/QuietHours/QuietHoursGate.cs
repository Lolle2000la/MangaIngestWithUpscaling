using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.QuietHours;

/// <summary>
///     Tells a host whether it is currently inside its configured quiet-hours window, and lets it
///     wait the window out. Shared by both hosts so the server's local upscale processor and each
///     remote worker's fetching loop gate on exactly the same rules.
/// </summary>
public interface IQuietHoursGate
{
    /// <summary>Whether new upscale work must not be started right now.</summary>
    bool IsQuiet { get; }

    /// <summary>
    ///     How long the current quiet window still runs, or <see langword="null" /> when it is not
    ///     quiet.
    /// </summary>
    TimeSpan? TimeUntilResume { get; }

    /// <summary>
    ///     Returns immediately when the gate is open; otherwise blocks until the window ends,
    ///     re-evaluating the clock as it goes so a wall-clock change cannot strand the caller.
    /// </summary>
    Task WaitUntilNotQuietAsync(CancellationToken cancellationToken);
}

/// <summary>
///     The <see cref="TimeProvider" />-based <see cref="IQuietHoursGate" />. The window is a local
///     wall-clock one, so the time of day comes from <see cref="TimeProvider.GetLocalNow" />, which
///     honours <see cref="TimeProvider.LocalTimeZone" /> (and can therefore be pinned by a test).
/// </summary>
public sealed class QuietHoursGate(IOptions<QuietHoursConfig> options, TimeProvider timeProvider)
    : IQuietHoursGate
{
    /// <summary>
    ///     Ceiling on a single wait. Re-evaluating at least once a minute means a clock jump (NTP, a
    ///     manual change, a host resumed from suspend) is observed promptly instead of after a
    ///     computed delay that was based on the old clock.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Floor on a single wait, so a remainder that rounds to nothing cannot turn the loop into a
    ///     spin that burns a core until the window ends.
    /// </summary>
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private QuietHoursConfig Config => options.Value;

    /// <summary>
    ///     Local time of day. It comes from <see cref="TimeProvider.GetLocalNow" /> rather than the
    ///     wall-clock APIs, which the UTC guard test forbids: the window is local, but it still has to
    ///     be a clock a test can pin.
    /// </summary>
    private TimeSpan TimeOfDay => timeProvider.GetLocalNow().TimeOfDay;

    public bool IsQuiet => Config.IsActive(TimeOfDay);

    public TimeSpan? TimeUntilResume => Config.TimeUntilEnd(TimeOfDay);

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

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
