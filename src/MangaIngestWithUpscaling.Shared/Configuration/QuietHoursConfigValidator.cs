using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
///     Rejects a <see cref="QuietHoursConfig" /> that cannot have meant what it says, so the mistake
///     fails the boot instead of silently scheduling the pause at an unintended hour.
///     <para>
///     A time-of-day outside a single day is not a value the window arithmetic can express: it would
///     compare as "outside the window" and quietly disable the feature. The zero-length window is
///     rejected only while the feature is enabled because it is genuinely ambiguous there — use
///     <c>Enabled = false</c> to turn the feature off instead. Everything is checked even when the
///     feature is disabled: an out-of-range value is a typo waiting to be enabled.
///     </para>
/// </summary>
public sealed class QuietHoursConfigValidator : IValidateOptions<QuietHoursConfig>
{
    public ValidateOptionsResult Validate(string? name, QuietHoursConfig options)
    {
        var invalid = new List<string>();

        ValidateTimeOfDay(invalid, nameof(QuietHoursConfig.Start), options.Start);
        ValidateTimeOfDay(invalid, nameof(QuietHoursConfig.End), options.End);

        if (options.Enabled && options.Start == options.End)
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Start)} and "
                    + $"{nameof(QuietHoursConfig.End)} are both {options.Start}: a zero-length window "
                    + $"is ambiguous; set {QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)} "
                    + "to false instead"
            );
        }

        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(invalid);
    }

    private static void ValidateTimeOfDay(List<string> invalid, string key, TimeSpan value)
    {
        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
        {
            invalid.Add(
                $"{QuietHoursConfig.Position}:{key} = {value}: expected a time of day from 00:00:00 "
                    + "up to but not including 24:00:00"
            );
        }
    }
}
