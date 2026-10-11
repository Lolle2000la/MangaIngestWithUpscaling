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
    public void Compare_ShorterNameSortsFirstWhenOneIsAPrefix()
    {
        string[] names = ["1.png", "1.png.jpg"];

        string[] ordered = names.OrderBy(n => n, NaturalEntryNameComparer.Instance).ToArray();

        Assert.Equal(["1.png", "1.png.jpg"], ordered);
    }
}
