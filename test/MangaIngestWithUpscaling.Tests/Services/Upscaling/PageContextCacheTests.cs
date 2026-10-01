using MangaIngestWithUpscaling.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

public class PageContextCacheTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SetAndTryGet_RoundTripsTheEntry()
    {
        var cache = new PageContextCache();
        PageContextCache.Entry entry = Entry();

        cache.Set(7, entry);

        Assert.True(cache.TryGet(7, out PageContextCache.Entry found));
        Assert.Same(entry, found);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Remove_DropsTheEntry()
    {
        var cache = new PageContextCache();
        cache.Set(7, Entry());

        cache.Remove(7);

        Assert.False(cache.TryGet(7, out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sweep_DropsEntriesUnusedSinceTheCutoff()
    {
        var cache = new PageContextCache();
        cache.Set(7, Entry());

        cache.Sweep(TimeSpan.FromSeconds(-1));

        Assert.False(cache.TryGet(7, out _));
    }

    private static PageContextCache.Entry Entry() =>
        new(
            "identity",
            "/source.cbz",
            new[] { new SpoolPageDescriptor(0, "001.jpg", "001.webp") },
            0,
            1,
            2,
            "/upscaled.cbz",
            Array.Empty<string>()
        );
}
