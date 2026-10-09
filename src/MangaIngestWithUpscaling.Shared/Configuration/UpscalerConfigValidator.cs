using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
/// Reports the size settings of an <see cref="UpscalerConfig"/> that are configured but cannot be
/// parsed, each as
/// <c>Upscaler:&lt;Name&gt; = "&lt;value&gt;": expected a byte count or a size such as 4GiB</c>.
/// Unset values are not reported: those mean "detect automatically", which is a valid choice.
/// </summary>
public static class UpscalerConfigSizeValidation
{
    public static IEnumerable<string> FindInvalidSizeSettings(this UpscalerConfig config)
    {
        foreach (
            (string Name, string? Value) setting in new (string, string?)[]
            {
                (nameof(UpscalerConfig.MemoryBudgetBytes), config.MemoryBudgetBytes),
                (nameof(UpscalerConfig.VramSafetyMarginBytes), config.VramSafetyMarginBytes),
                (
                    nameof(UpscalerConfig.VramExclusiveThresholdBytes),
                    config.VramExclusiveThresholdBytes
                ),
                (nameof(UpscalerConfig.MaxSpoolBytesPerTask), config.MaxSpoolBytesPerTask),
            }
        )
        {
            if (string.IsNullOrWhiteSpace(setting.Value))
            {
                continue;
            }

            if (!ByteSize.TryParse(setting.Value, out _))
            {
                yield return $"{UpscalerConfig.Position}:{setting.Name} = \"{setting.Value}\": expected a byte count or a size such as 4GiB, 700MiB or 1024";
            }
        }
    }
}

/// <summary>
/// Rejects an <see cref="UpscalerConfig"/> that cannot have meant what it says.
/// <para>
/// Size settings are strings so they can be written as <c>4GiB</c> or <c>367001600</c>, which makes
/// a typo (<c>4GiBX</c>, <c>12 Gigabytes</c>) indistinguishable from "not configured" at the point of
/// use. Silently falling back to auto-detection would drop an explicit override — the exact thing
/// the setting exists to express — so it fails at startup instead, where the offending line is still
/// findable in the log before the first chapter is upscaled.
/// </para>
/// </summary>
public sealed class UpscalerConfigValidator : IValidateOptions<UpscalerConfig>
{
    public ValidateOptionsResult Validate(string? name, UpscalerConfig options)
    {
        List<string> invalid = options.FindInvalidSizeSettings().ToList();
        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(invalid);
    }
}
