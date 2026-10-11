using System.IO.Compression;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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
    public void BuildPageDescriptors_OrdersPagesNaturallyAndUsesTheProfileExtension()
    {
        string directory = Directory.CreateTempSubdirectory("manifest").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("10.png", new byte[] { 1 }),
                ("2.png", new byte[] { 2 }),
                ("001.jpg", new byte[] { 3 }),
                ("ComicInfo.xml", new byte[] { 4 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            // Natural order, matching the whole-CBZ path: an ordinal archive order would be
            // 10, 2, 001. The descriptor index follows this order too, so streaming and the
            // assembled archive agree on page sequence.
            Assert.Equal(3, pages.Count);
            Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p.Index));
            Assert.Equal(new[] { "001.jpg", "2.png", "10.png" }, pages.Select(p => p.SourceName));
            Assert.Equal(
                new[] { "001.webp", "2.webp", "10.webp" },
                pages.Select(p => p.OutputName)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_KeepsPagesWithAColonInTheName()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_colon").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("Chapter 1: Intro/001.jpg", new byte[] { 1 }),
                ("002.jpg", new byte[] { 2 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            // A colon is legal on Linux; rejecting it silently dropped the page while the chapter was
            // still reported upscaled.
            Assert.Equal(2, pages.Count);
            Assert.Contains("Chapter 1: Intro/001.jpg", pages.Select(p => p.SourceName));
            Assert.Contains("002.jpg", pages.Select(p => p.SourceName));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_KeepsBenignDotAndEmptySegments()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_dot").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("./001.jpg", new byte[] { 1 }),
                ("a//b.jpg", new byte[] { 2 }),
                ("a/./c.jpg", new byte[] { 3 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            // "." and empty components collapse harmlessly; dropping them lost pages silently.
            Assert.Equal(3, pages.Count);
            Assert.Equal(
                new[] { "./001.jpg", "a//b.jpg", "a/./c.jpg" }.OrderBy(
                    x => x,
                    StringComparer.Ordinal
                ),
                pages.Select(p => p.SourceName).OrderBy(x => x, StringComparer.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_PreservesFoldersAndDedupesRepeatedEntries()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_nested").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("ch1/001.jpg", new byte[] { 1 }),
                ("ch2/001.jpg", new byte[] { 2 }),
                ("ch1/001.jpg", new byte[] { 3 }) // duplicate entry name
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            // The duplicate is dropped, and same-stemmed pages in different folders stay distinct
            // (matching the whole-CBZ worker, which preserves entry paths).
            Assert.Equal(2, pages.Count);
            Assert.Equal(new[] { 0, 1 }, pages.Select(p => p.Index));
            Assert.Equal("ch1/001.jpg", pages[0].SourceName);
            Assert.Equal("ch1/001.webp", pages[0].OutputName);
            Assert.Equal("ch2/001.jpg", pages[1].SourceName);
            Assert.Equal("ch2/001.webp", pages[1].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SharedCompressionFormat.Webp, "webp")]
    [InlineData(SharedCompressionFormat.Png, "png")]
    [InlineData(SharedCompressionFormat.Jpg, "jpg")]
    [InlineData(SharedCompressionFormat.Avif, "avif")]
    public void BuildPageDescriptors_UsesTheCanonicalExtensionForEveryFormat(
        SharedCompressionFormat format,
        string expected
    )
    {
        // Both upscale paths must map a format to the same extension. The streamed path delegates to
        // MangaJaNaiWorkerClient.ToExtension (the whole-CBZ path's mapping), so pin them together
        // here rather than letting ".jpg" and ".jpeg" drift apart again.
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToExtension(format).TrimStart('.'));

        string directory = Directory.CreateTempSubdirectory("manifest_ext").FullName;
        try
        {
            string source = CreateCbz(directory, ("001.jpg", new byte[] { 1 }));

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile(format)
            );

            Assert.Equal($"001.{expected}", pages[0].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_DisambiguatesCollidingOutputNames()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_collision").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("001.jpg", new byte[] { 1 }),
                ("001.png", new byte[] { 2 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            // Two entries share a stem, so both would map to 001.webp; the second must be renamed
            // rather than silently dropped during assembly.
            Assert.Equal(2, pages.Count);
            Assert.Equal("001.webp", pages[0].OutputName);
            Assert.Equal("001_1.webp", pages[1].OutputName);
            Assert.NotEqual(pages[0].OutputName, pages[1].OutputName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildRepairPageDescriptors_FlattensNestedOutputNames()
    {
        string directory = Directory.CreateTempSubdirectory("manifest_repair_nested").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("ch1/001.jpg", new byte[] { 1 }),
                ("ch1/002.jpg", new byte[] { 2 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildRepairPageDescriptors(
                source,
                new[] { "002" },
                Profile()
            );

            // The source entry keeps its folder so it can be fetched, but the output name is flat:
            // the repair merge copies top-level files by name and the whole-CBZ repair path flattens.
            Assert.Single(pages);
            Assert.Equal("ch1/002.jpg", pages[0].SourceName);
            Assert.Equal("002.webp", pages[0].OutputName);
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

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildRepairPageDescriptors(
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

            string webp = PageManifestBuilder.ComputeIdentity(source, Profile(), Preprocessing());
            string png = PageManifestBuilder.ComputeIdentity(
                source,
                Profile(SharedCompressionFormat.Png),
                Preprocessing()
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
    public void ComputeIdentity_ChangesWithPreprocessing()
    {
        // The server owns preprocessing and folds it into the content identity, so a change resets
        // the spool (the worker's engine identity deliberately excludes it).
        string directory = Directory.CreateTempSubdirectory("identity_prep").FullName;
        try
        {
            string source = CreateCbz(directory, ("001.jpg", new byte[] { 1 }));

            string none = PageManifestBuilder.ComputeIdentity(source, Profile(), Preprocessing());
            string resized = PageManifestBuilder.ComputeIdentity(
                source,
                Profile(),
                new ImagePreprocessingOptions { MaxDimension = 1600 }
            );

            Assert.NotEqual(none, resized);
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

            string one = PageManifestBuilder.ComputeRepairIdentity(
                source,
                upscaled,
                Profile(),
                new[] { "002" },
                Preprocessing()
            );
            string two = PageManifestBuilder.ComputeRepairIdentity(
                source,
                upscaled,
                Profile(),
                new[] { "001", "002" },
                Preprocessing()
            );

            Assert.NotEqual(one, two);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageDescriptors_DropsEntriesThatCouldEscapeTheArchive()
    {
        // Path traversal must be rejected through the descriptor builder, not only at IsSafeEntryName,
        // so the assembled archive can never be made to write outside its target.
        string directory = Directory.CreateTempSubdirectory("manifest_traversal").FullName;
        try
        {
            string source = CreateCbz(
                directory,
                ("../evil.jpg", new byte[] { 1 }),
                ("/absolute.jpg", new byte[] { 2 }),
                ("C:\\drive.jpg", new byte[] { 3 }),
                ("..\\windows-traversal.jpg", new byte[] { 4 }),
                ("ok/001.jpg", new byte[] { 5 })
            );

            List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildPageDescriptors(
                source,
                Profile()
            );

            Assert.Single(pages);
            Assert.Equal("ok/001.jpg", pages[0].SourceName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ImagePreprocessingOptions Preprocessing() => new();

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
