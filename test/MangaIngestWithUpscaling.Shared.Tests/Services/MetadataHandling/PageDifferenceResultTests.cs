using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.MetadataHandling;

public class PageDifferenceResultTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void AreEqual_IsFalseWhenAnArchiveCouldNotBeRead()
    {
        // The footgun that produced the silent-repair and delete-on-read-failure bugs: an unreadable
        // archive reports empty page lists, which previously read as "no differences".
        Assert.False(new PageDifferenceResult([], []) { Corrupt = true }.AreEqual);
        Assert.False(new PageDifferenceResult([], []) { ReadFailed = true }.AreEqual);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanRepair_IsFalseWhenAnArchiveCouldNotBeRead()
    {
        Assert.False(new PageDifferenceResult(["001.jpg"], []) { Corrupt = true }.CanRepair);
        Assert.False(new PageDifferenceResult(["001.jpg"], []) { ReadFailed = true }.CanRepair);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AreEqualAndCanRepair_AreUnchangedForReadableArchives()
    {
        Assert.True(new PageDifferenceResult([], []).AreEqual);
        Assert.False(new PageDifferenceResult([], []).CanRepair);

        Assert.False(new PageDifferenceResult(["001.jpg"], []).AreEqual);
        Assert.True(new PageDifferenceResult(["001.jpg"], []).CanRepair);
    }
}
