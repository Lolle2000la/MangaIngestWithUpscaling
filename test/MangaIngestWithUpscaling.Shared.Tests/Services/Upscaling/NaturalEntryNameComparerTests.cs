using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class NaturalEntryNameComparerTests
{
    [Fact]
    public void Compare_OrdersDigitRunsNumerically()
    {
        // Ordinal ordering would give 1, 10, 2.
        string[] names = ["10.png", "2.png", "1.png"];

        string[] ordered = names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray();

        Assert.Equal(["1.png", "2.png", "10.png"], ordered);
    }

    [Fact]
    public void Compare_PreservesFolderPrefixInOrdering()
    {
        string[] names = ["ch/10.png", "ch/2.png", "ch/1.png"];

        string[] ordered = names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray();

        Assert.Equal(["ch/1.png", "ch/2.png", "ch/10.png"], ordered);
    }

    [Fact]
    public void Compare_IsCaseInsensitiveOnTheTextParts()
    {
        string[] names = ["B.png", "a.png"];

        string[] ordered = names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray();

        Assert.Equal(["a.png", "B.png"], ordered);
    }

    [Fact]
    public void Compare_NumericallyEqualButTextuallyDifferentNamesAreNotEqual()
    {
        // "1.png" and "01.png" parse to the same number. Without an ordinal tiebreak they compare
        // equal, and a consumer that treats the comparer as a total order — a set, a distinct, a
        // sorted dictionary — would collapse two different pages into one.
        Assert.NotEqual(0, NaturalEntryNameComparer.Instance.Compare("1.png", "01.png"));

        // Still a stable, distinct ordering when both are present: ordinal, so "01" precedes "1".
        string[] ordered = new[] { "01.png", "1.png" }
            .OrderBy(n => n, NaturalEntryNameComparer.Instance)
            .ToArray();
        Assert.Equal(2, ordered.Distinct().Count());
        Assert.Equal(["01.png", "1.png"], ordered);
    }

    [Fact]
    public void Compare_ShorterNameSortsFirstWhenOneIsAPrefix()
    {
        string[] names = ["1.png", "1.png.jpg"];

        string[] ordered = names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray();

        Assert.Equal(["1.png", "1.png.jpg"], ordered);
    }
}
