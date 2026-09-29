using System.IO.Compression;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Services.Upscaling;
using Xunit;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using SharedUpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

/// <summary>
/// Covers the manifest builders behind page streaming, including the repair variant that lists only
/// the pages missing from the existing upscaled chapter.
/// </summary>
public class PageManifestBuilderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_UsesArchiveOrderAndProfileExtension()
    {
        string directory = Directory.CreateTempSubdirectory("manifest").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("001.jpg", new byte[] { 1 }),
                ("002.png", new byte[] { 2 }),
                ("ComicInfo.xml", new byte[] { 3 })
            );

            List<SpoolPageDescriptor> pages = UpscalingDistributionService.BuildPageDescriptors(
                source,
                Profile()
            );

            Assert.Equal(2, pages.Count);
            Assert.Equal(new[] { 0, 1 }, pages.Select(p => p.Index));
            Assert.Equal("001.jpg", pages[0].SourceName);
            Assert.Equal("001.webp", pages[0].OutputName);
            Assert.Equal("002.png", pages[1].SourceName);
            Assert.Equal("002.webp", pages[1].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_MapsJpgToJpegExtension()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_ext").FullName;
        try
        {
            string source = CreateCbz(directory, ("001.jpg", new byte[] { 1 }));

            List<SpoolPageDescriptor> pages = UpscalingDistributionService.BuildPageDescriptors(
                source,
                Profile(SharedCompressionFormat.Jpg)
            );

            Assert.Equal("001.jpeg", pages[0].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildRepairPageDescriptors_ListsOnlyTheMissingPages()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_repair").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("001.jpg", new byte[] { 1 }),
                ("002.jpg", new byte[] { 2 }),
                ("003.jpg", new byte[] { 3 })
            );

            List<SpoolPageDescriptor> pages =
                UpscalingDistributionService.BuildRepairPageDescriptors(
                    source,
                    new[] { "002", "003" },
                    Profile()
                );

            Assert.Equal(2, pages.Count);
            Assert.Equal(new[] { 0, 1 }, pages.Select(p => p.Index));
            Assert.Equal("002.jpg", pages[0].SourceName);
            Assert.Equal("002.webp", pages[0].OutputName);
            Assert.Equal("003.webp", pages[1].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeIdentity_ChangesWithProfile()
    {
        string directory = Directory.CreateTempSubdirectory("identity").FullName;
        try
        {
            string source = CreateCbz(directory, ("001.jpg", new byte[] { 1 }));

            string webp = UpscalingDistributionService.ComputeIdentity(source, Profile());
            string png = UpscalingDistributionService.ComputeIdentity(
                source,
                Profile(SharedCompressionFormat.Png)
            );

            Assert.NotEqual(webp, png);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeRepairIdentity_ChangesWithMissingPageSet()
    {
        string directory = Directory.CreateTempSubdirectory("identity_repair").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("001.jpg", new byte[] { 1 }),
                ("002.jpg", new byte[] { 2 })
            );
            string upscaled = CreateCbz(directory, ("001.webp", new byte[] { 9 }));

            string one = UpscalingDistributionService.ComputeRepairIdentity(
                source,
                upscaled,
                Profile(),
                new[] { "002" }
            );
            string two = UpscalingDistributionService.ComputeRepairIdentity(
                source,
                upscaled,
                Profile(),
                new[] { "001", "002" }
            );

            Assert.NotEqual(one, two);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static SharedUpscalerProfile Profile(
        SharedCompressionFormat format = SharedCompressionFormat.Webp
    ) =>
        new()
        {
            Name = "test",
            CompressionFormat = format,
            ScalingFactor = SharedScaleFactor.TwoX,
            Quality = 80,
        };

    private static string CreateCbz(string directory, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(directory, $"source_{Guid.NewGuid():N}.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }
}
