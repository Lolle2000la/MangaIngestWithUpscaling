using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
///     Rejects a <see cref="QuietHoursConfig" /> that cannot have meant what it says, so the mistake
///     fails the boot instead of silently scheduling the pause at an unintended hour — or not at all.
///     <para>
///     The window arithmetic cannot express a time of day outside a single day (such a value simply
///     compares as "outside the window"), so those are rejected here; the same goes for a zero-length
///     window, an unreadable <see cref="QuietHoursConfig.TimeZone" /> and the two ambiguous shapes
///     (half a daily pair, or a daily pair plus an explicit list). All of it is checked even when the
///     feature is disabled: a typo is a typo whether or not it is enabled today.
///     </para>
/// </summary>
public sealed class QuietHoursConfigValidator : IValidateOptions<QuietHoursConfig>
{
    public ValidateOptionsResult Validate(string? name, QuietHoursConfig options)
    {
        var invalid = new List<string>();

        ValidateTimeZone(invalid, options.TimeZone);
        ValidateDailyPair(invalid, options);
        ValidateWindows(invalid, options);

        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(invalid);
    }

    private static void ValidateTimeZone(List<string> invalid, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException ex)
        {
            invalid.Add(InvalidZone(timeZone, ex));
        }
        catch (InvalidTimeZoneException ex)
        {
            // The zone exists but its data cannot be used (corrupt or unsupported transition rules).
            invalid.Add(InvalidZone(timeZone, ex));
        }
    }

    private static string InvalidZone(string timeZone, Exception ex) =>
        $"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.TimeZone)} = \"{timeZone}\": "
        + $"not a time zone this host knows ({ex.Message})";

    private static void ValidateDailyPair(List<string> invalid, QuietHoursConfig options)
    {
        if (options.Start is null != options.End is null)
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Start)} and "
                    + $"{nameof(QuietHoursConfig.End)} must be set together; only "
                    + $"{(options.Start is null ? nameof(QuietHoursConfig.End) : nameof(QuietHoursConfig.Start))} is set"
            );
        }

        ValidateTimeOfDay(
            invalid,
            nameof(QuietHoursConfig.Start),
            options.Start,
            TimeSpan.Zero,
            TimeSpan.FromDays(1)
        );
        ValidateTimeOfDay(
            invalid,
            nameof(QuietHoursConfig.End),
            options.End,
            TimeSpan.Zero,
            TimeSpan.FromDays(1)
        );

        if (options.Start is TimeSpan start && options.End is TimeSpan end && start == end)
        {
            invalid.Add(
                ZeroLength(
                    $"{nameof(QuietHoursConfig.Start)}/{nameof(QuietHoursConfig.End)}",
                    start
                )
            );
        }

        if (options.Windows is { Count: > 0 } && options.Start is not null)
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}: specify either {nameof(QuietHoursConfig.Start)}/"
                    + $"{nameof(QuietHoursConfig.End)} or {nameof(QuietHoursConfig.Windows)}, not both"
            );
        }
    }

    private static void ValidateWindows(List<string> invalid, QuietHoursConfig options)
    {
        for (int i = 0; i < (options.Windows?.Count ?? 0); i++)
        {
            QuietWindow window = options.Windows![i];
            ValidateTimeOfDay(
                invalid,
                $"{nameof(QuietHoursConfig.Windows)}[{i}].{nameof(QuietWindow.Start)}",
                window.Start,
                TimeSpan.Zero,
                TimeSpan.FromDays(1)
            );
            ValidateTimeOfDay(
                invalid,
                $"{nameof(QuietHoursConfig.Windows)}[{i}].{nameof(QuietWindow.End)}",
                window.End,
                TimeSpan.Zero,
                TimeSpan.FromDays(1)
            );

            if (window.Start == window.End)
            {
                invalid.Add(ZeroLength($"{nameof(QuietHoursConfig.Windows)}[{i}]", window.Start));
            }
        }

        if (
            options.Enabled
            && options.Windows is not { Count: > 0 }
            && options.Start is null
            && options.End is null
        )
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)} is true but no window "
                    + $"is configured; set {nameof(QuietHoursConfig.Start)}/{nameof(QuietHoursConfig.End)} "
                    + $"or {nameof(QuietHoursConfig.Windows)}, or set "
                    + $"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)} to false"
            );
        }
    }

    private static void ValidateTimeOfDay(
        List<string> invalid,
        string key,
        TimeSpan? value,
        TimeSpan min,
        TimeSpan max
    )
    {
        if (value is TimeSpan timeOfDay && (timeOfDay < min || timeOfDay >= max))
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}:{key} = {timeOfDay}: expected a time of day from 00:00:00 "
                    + "up to but not including 24:00:00"
            );
        }
    }

    private static string ZeroLength(string key, TimeSpan value) =>
        $"{QuietHoursConfig.Position}:{key}: {nameof(QuietWindow.Start)} and {nameof(QuietWindow.End)} "
        + $"are both {value}, a zero-length window that can never be active; give it a non-zero length, "
        + $"or set {QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)} to false";
}
