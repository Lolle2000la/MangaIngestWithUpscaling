using System.Globalization;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Helpers;

/// <summary>
/// The app orders chapter pages with <see cref="NaturalEntryNameComparer" /> (through the thin
/// <see cref="NaturalSortComparer{T}" /> where a key selector is used). These pin the two properties
/// the old app-local comparer got wrong: the numeric parse is culture-independent, and numerically
/// equal names ("1.png" vs "01.png") still compare unequal.
/// </summary>
public class NaturalSortComparerTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Compare_IsCultureIndependentForFractionalDigitRuns(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // German and Turkish treat '.' as a group separator, so a culture-sensitive parse with
            // AllowThousands reads "1.5" as 15 and orders "12.png" before it. The canonical comparison
            // parses invariant, so 1.5 sorts before 12.
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            string[] names = ["12.png", "1.5.png"];

            Assert.Equal(
                ["1.5.png", "12.png"],
                names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray()
            );
            Assert.Equal(
                ["1.5.png", "12.png"],
                names.OrderBy(n => n, new NaturalSortComparer<string>(s => s)).ToArray()
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Compare_BreaksNumericTiesOrdinally()
    {
        // "1.png" and "01.png" parse to the same number. Without the ordinal tie-break they compare
        // equal, and a consumer treating the comparer as a total order (a set, a distinct, a sorted
        // dictionary) would collapse two different pages into one.
        Assert.NotEqual(0, NaturalEntryNameComparer.Instance.Compare("1.png", "01.png"));
        Assert.NotEqual(0, new NaturalSortComparer<string>(s => s).Compare("1.png", "01.png"));

        // Still a stable, distinct ordering: ordinal, so "01" precedes "1".
        string[] ordered = new[] { "1.png", "01.png" }
            .OrderBy(n => n, new NaturalSortComparer<string>(s => s))
            .ToArray();
        Assert.Equal(["01.png", "1.png"], ordered);
    }

    [Fact]
    public void Compare_DelegatesTheTotalOrderToTheSharedComparer()
    {
        // The wrapper carries no ordering logic of its own, so it must expose the shared comparer's
        // total order — including the mixed-run case, where a digit run sorts before a non-digit run
        // ("NaN" is a non-digit run the numeric parser would otherwise accept).
        var comparer = new NaturalSortComparer<string>(s => s);

        Assert.True(comparer.Compare("0", "NaN") < 0);

        List<string> names = ["10", "2", "1x", "NaN", "a", "0", "Infinity"];
        names.Sort(comparer);

        Assert.Equal(["0", "1x", "2", "10", "a", "Infinity", "NaN"], names);
    }
}
