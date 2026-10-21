using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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

        // A non-finite number is not a plausible one and does not survive CalculateVramBudget's clamp:
        // NaN compares false against everything, so it would poison the budget instead of being clamped
        // into range.
        if (config.VramUtilizationFraction is double utilization)
        {
            if (!double.IsFinite(utilization))
            {
                yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.VramUtilizationFraction)} = {utilization}: expected a finite fraction such as 0.97, or unset for automatic";
            }
            // Out-of-range values are only silently clamped by CalculateVramBudget (to [0.1, 1.0]), so
            // the fraction actually applied would not be the one configured. The documented range
            // fails the boot instead, the same reason the preprocessing ranges are checked here.
            // Exactly 0.0 is allowed and means "automatic" (CalculateVramBudget treats a non-positive
            // fraction as unset); a positive fraction below 0.1 is not, because the clamp would apply
            // 0.1 — the configured value and the applied one have to be the same.
            else if (utilization != 0.0 && (utilization < 0.1 || utilization > 1.0))
            {
                yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.VramUtilizationFraction)} = {utilization}: expected a fraction between 0.1 and 1.0, exactly 0 for automatic, or unset";
            }
        }

        // TileSize > 0 is a manual maximum that bypasses PlanTileSplit, so it is the one tile size the
        // planner's own quality floor never sees. The floor above (4 * DefaultTilePad) is not the
        // blender's: TileBlender.RequireBlendRegion is unreachable from this path — TileSplit.For is
        // free to shrink the tiles below the bound (a page only slightly wider than it), but the
        // blender narrows each tile's overlap to what the previous tile actually had
        // (TileBlender.AddTile's _lastEndOverlap branch), which keeps even a 1 px remainder inside the
        // region. What a tile this small really costs is described by MinimumTileSize and
        // EmergencyTileFloor: below the overlap/blending overhead per pixel and the loss of
        // receptive-field context on manga screentones outweigh the memory saving, so the model is
        // asked to upscale padding-dominated postage stamps. 0 (automatic) and -1 (single pass) are
        // unaffected: the planner keeps its own quality floor.
        if (config.TileSize > 0 && config.TileSize < 4 * OnnxTiler.DefaultTilePad)
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.TileSize)} = {config.TileSize}: a manual tile size below {4 * OnnxTiler.DefaultTilePad} px is padding-dominated — the blend region alone is {2 * OnnxTiler.DefaultTilePad} px, so most of such a tile is overlap, and the model loses the receptive-field context it needs on screentones; use 0 for automatic, -1 for a single pass, or at least {4 * OnnxTiler.DefaultTilePad}";
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
/// <para>
/// The preprocessing values are checked here for the same reason: <see cref="ImageResizeService"/>
/// rejects them per image, so an invalid one would otherwise fail the first chapter of the first task
/// instead of the boot. The ranges mirror that check, including applying the smart-downscale ones
/// only while the feature is enabled — a nonsense value on a disabled feature is inert.
/// </para>
/// <para>
/// The architecture preference is checked for the same reason again: an unrecognised name would
/// silently leave the built-in order in place, which is the opposite of what an operator asking for
/// a specific order wants.
/// </para>
/// </summary>
public sealed class UpscalerConfigValidator : IValidateOptions<UpscalerConfig>
{
    public ValidateOptionsResult Validate(string? name, UpscalerConfig options)
    {
        List<string> invalid = options.FindInvalidSizeSettings().ToList();
        invalid.AddRange(options.FindInvalidPreprocessingSettings());
        invalid.AddRange(options.FindInvalidArchitecturePreference());
        invalid.AddRange(options.FindInvalidGpuBackendSettings());
        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(invalid);
    }
}

/// <summary>
/// The GPU-backend half of <see cref="UpscalerConfigValidator"/>. A numeric value binds to an
/// undefined enum member without an error, and the session factory then disagrees with itself
/// about what it means: the identity, the calibration fingerprint and the logs report CPU while
/// the Auto discovery attaches a GPU — so an undefined value fails the boot here instead.
/// </summary>
public static class UpscalerConfigBackendValidation
{
    public static IEnumerable<string> FindInvalidGpuBackendSettings(this UpscalerConfig config)
    {
        if (!Enum.IsDefined(config.PreferredGpuBackend))
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.PreferredGpuBackend)} = {(int)config.PreferredGpuBackend}: not a defined backend. Set it by name — Auto, CPU, CUDA, DirectML, OpenVINO, MIGraphX or WebGPU — not by number";
        }
    }
}

/// <summary>
/// The model-architecture-preference half of <see cref="UpscalerConfigValidator"/>. The engine's
/// <see cref="ModelArchitecturePreference.Resolve"/> skips an unrecognised name, so without this the
/// setting would be silently ignored rather than the boot failing.
/// </summary>
public static class UpscalerConfigArchitectureValidation
{
    public static IEnumerable<string> FindInvalidArchitecturePreference(this UpscalerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.PreferredModelArchitectures))
        {
            yield break;
        }

        foreach (
            string token in config.PreferredModelArchitectures.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (ModelArchitecturePreference.Parse(token) is null)
            {
                yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.PreferredModelArchitectures)} = \"{config.PreferredModelArchitectures}\": '{token}' is not a known architecture. Expected a comma-separated list of ESRGAN, SPAN, FDAT_M, FDAT_XL, DAT2 and HAT_L, most preferred first";
            }
        }
    }
}

/// <summary>
/// The preprocessing half of <see cref="UpscalerConfigValidator"/>. Kept beside it so the two checks
/// on the same section are read together, and so <see cref="ImageResizeService.Validate"/> has a
/// boot-time counterpart it can be compared against.
/// </summary>
public static class UpscalerConfigPreprocessingValidation
{
    public static IEnumerable<string> FindInvalidPreprocessingSettings(this UpscalerConfig config)
    {
        if (config.MaxDimensionBeforeUpscaling is < 0)
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.MaxDimensionBeforeUpscaling)} = {config.MaxDimensionBeforeUpscaling}: expected null, 0 or a positive maximum dimension";
        }

        // Checked before the smart-downscale gate: the rules apply regardless of it. libvips
        // clamps or ignores an out-of-range q rather than erroring, so without this a typo'd
        // quality is silently applied as something other than what was configured.
        foreach (ImageFormatConversionRule rule in config.EffectiveImageFormatConversionRules)
        {
            if (rule.Quality is not null and (< 1 or > 100))
            {
                yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.ImageFormatConversionRules)}[{rule.FromFormat} -> {rule.ToFormat}].{nameof(rule.Quality)} = {rule.Quality}: expected null or 1-100";
            }
        }

        if (!config.EnableSmartDownscale)
        {
            yield break;
        }

        // A non-finite double is not a plausible number and does not behave like one: every comparison
        // against NaN is false, so the range checks below would let it through, and an infinite
        // threshold would classify every image as a cheap upscale.
        if (!double.IsFinite(config.SmartDownscaleThreshold))
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.SmartDownscaleThreshold)} = {config.SmartDownscaleThreshold}: expected a finite Laplacian standard deviation";
        }
        else if (config.SmartDownscaleThreshold <= 0)
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.SmartDownscaleThreshold)} = {config.SmartDownscaleThreshold}: expected a positive Laplacian standard deviation";
        }

        if (!double.IsFinite(config.SmartDownscaleFactor))
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.SmartDownscaleFactor)} = {config.SmartDownscaleFactor}: expected a finite fraction strictly between 0 and 1";
        }
        else if (config.SmartDownscaleFactor is <= 0 or >= 1)
        {
            yield return $"{UpscalerConfig.Position}:{nameof(UpscalerConfig.SmartDownscaleFactor)} = {config.SmartDownscaleFactor}: expected a fraction strictly between 0 and 1";
        }
    }
}
