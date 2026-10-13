namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Scales a base inactivity timeout by the size of the operation (a whole chapter, its largest page,
/// or the page being detected), saturating instead of overflowing. The pixel count can come from a
/// crafted archive's declared sizes and be absurd, and <see cref="TimeSpan" /> multiplication throws
/// <see cref="OverflowException" /> once the product passes <see cref="TimeSpan.MaxValue" /> — an
/// exception that is neither a memory nor an IO failure, so it is misclassified as a permanent fault.
/// Computing in ticks lets the product be capped before the cast, turning an instant overflow into
/// merely a very long timeout.
/// </summary>
public static class ScaledTimeout
{
    /// <summary>Base timeout scaled by <c>max(1, pixelCount / 1_000_000)</c>.</summary>
    public static TimeSpan FromPixelCount(TimeSpan basis, double pixelCount) =>
        Scale(basis, Math.Max(1.0, pixelCount / 1_000_000.0));

    /// <summary>Saturating equivalent of <c>basis * scalingFactor</c>.</summary>
    public static TimeSpan Scale(TimeSpan basis, double scalingFactor)
    {
        double ticks = basis.Ticks * scalingFactor;
        if (double.IsNaN(ticks) || ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks((long)Math.Min(ticks, TimeSpan.MaxValue.Ticks));
    }
}
