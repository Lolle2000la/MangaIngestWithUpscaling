using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class ScaledTimeoutTests
{
    /// <summary>
    /// <see cref="TimeSpan.MaxValue" /> ticks as a double. long.MaxValue rounds up to 2^63 here, one
    /// past what a long can hold — the value the pre-fix saturating branch cast.
    /// </summary>
    private static readonly double Cap = (double)TimeSpan.MaxValue.Ticks;

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    [InlineData(1e30)]
    [InlineData(1e18)]
    [Trait("Category", "Unit")]
    public void Scale_HugeFactor_SaturatesAtTimeSpanMaxValue(double factor)
    {
        TimeSpan result = ScaledTimeout.Scale(TimeSpan.FromMinutes(1), factor);

        Assert.Equal(TimeSpan.MaxValue, result);
        Assert.True(result > TimeSpan.Zero, "a negative span would silently disable the watchdog.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Scale_TimeSpanMaxValueByOne_StaysMaxValueAndPositive()
    {
        // The saturating branch's own boundary: (double)TimeSpan.MaxValue.Ticks rounds up to 2^63, so
        // the pre-fix unchecked (long) cast produced long.MinValue on runtimes whose double->long
        // conversions wrap — TimeSpan.MinValue, a negative watchdog. A positive TimeSpan.MaxValue is
        // the only acceptable answer.
        TimeSpan result = ScaledTimeout.Scale(TimeSpan.MaxValue, 1.0);

        Assert.Equal(TimeSpan.MaxValue, result);
        Assert.True(result > TimeSpan.Zero);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Scale_FactorExactlyAtTheCap_Saturates()
    {
        const long basisTicks = 1_000_000_000;
        double factor = Cap / basisTicks;

        TimeSpan result = ScaledTimeout.Scale(TimeSpan.FromTicks(basisTicks), factor);

        Assert.Equal(TimeSpan.MaxValue, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Scale_JustBelowTheCap_IsExactAndStillPositive()
    {
        // The largest double strictly below the cap is 2^63 - 1024, which does fit a long. A product
        // there must be returned exactly rather than clamped, and must stay positive.
        const long basisTicks = 1_000_000_000;
        double factor = (Cap - 2048) / basisTicks;
        long expectedTicks = (long)(basisTicks * factor);

        TimeSpan result = ScaledTimeout.Scale(TimeSpan.FromTicks(basisTicks), factor);

        Assert.True(result > TimeSpan.Zero);
        Assert.True(result < TimeSpan.MaxValue);
        Assert.Equal(expectedTicks, result.Ticks);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Scale_OrdinaryFactor_IsLinear() =>
        Assert.Equal(TimeSpan.FromMinutes(2), ScaledTimeout.Scale(TimeSpan.FromMinutes(1), 2.0));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [Trait("Category", "Unit")]
    public void Scale_NonPositiveOrNaN_YieldsZero(double factor) =>
        Assert.Equal(TimeSpan.Zero, ScaledTimeout.Scale(TimeSpan.FromMinutes(1), factor));

    [Fact]
    [Trait("Category", "Unit")]
    public void FromPixelCount_ScalesByMegapixelsWithAFloor()
    {
        // Under a megapixel the factor is floored to 1, above it the page buys proportionally more time.
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            ScaledTimeout.FromPixelCount(TimeSpan.FromMinutes(1), 500_000)
        );
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            ScaledTimeout.FromPixelCount(TimeSpan.FromMinutes(1), 2_000_000)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FromPixelCount_AbsurdPixelCount_SaturatesInsteadOfGoingNegative()
    {
        TimeSpan result = ScaledTimeout.FromPixelCount(TimeSpan.FromMinutes(1), double.MaxValue);

        Assert.Equal(TimeSpan.MaxValue, result);
        Assert.True(result > TimeSpan.Zero);
    }
}
