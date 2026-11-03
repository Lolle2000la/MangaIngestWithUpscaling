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

        // Guard in double *before* the cast. (double)TimeSpan.MaxValue.Ticks rounds up to 2^63, one
        // past long.MaxValue, so a value at or above it cannot be represented as a tick count; an
        // unchecked (long) cast of such a value is unspecified and wrapped to long.MinValue before
        // .NET 9's saturating floating-point-to-integer conversions — a NEGATIVE span that silently
        // disables the inactivity watchdog and makes Task.WaitAsync throw
        // ArgumentOutOfRangeException (misclassified as permanent). Compare first, then cast only a
        // value known to be strictly below 2^63. The same double-vs-long trap the ByteSize parser
        // documents and pins (Parse_RejectsAValueOneAboveLongMaxValue).
        if (ticks >= (double)TimeSpan.MaxValue.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}
