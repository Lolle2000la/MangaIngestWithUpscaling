using System.IO.Compression;
using MangaIngestWithUpscaling.Services.Upscaling;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

public class PageStreamSpoolTests
{
    private readonly PageStreamSpool _spool = new(Substitute.For<ILogger<PageStreamSpool>>());

    private static string CreateSourceCbz(string directory)
    {
        string path = Path.Combine(directory, "source.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
        WriteEntry(zip, "002.jpg", new byte[] { 4, 5, 6 });
        WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using Stream stream = entry.Open();
        stream.Write(data);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WritePage_Assemble_ReplacesImagesAndCopiesOtherEntries()
    {
        string directory = Directory.CreateTempSubdirectory("spool_test").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            var pages = new List<SpoolPageDescriptor>
            {
                new(0, "001.jpg", "001.webp"),
                new(1, "002.jpg", "002.webp"),
            };
            PageStreamSession session = _spool.GetOrCreateSession(1, "identity", pages.Count);

            await _spool.WritePageAsync(
                session,
                0,
                new MemoryStream(new byte[] { 10, 11 }),
                TestContext.Current.CancellationToken
            );
            Assert.False(_spool.IsComplete(session));

            await _spool.WritePageAsync(
                session,
                1,
                new MemoryStream(new byte[] { 12, 13 }),
                TestContext.Current.CancellationToken
            );
            Assert.True(_spool.IsComplete(session));
            Assert.Equal(new[] { 0, 1 }, _spool.GetCompletedPages(session).OrderBy(i => i));

            string destination = Path.Combine(directory, "out.cbz");
            _spool.Assemble(session, source, pages, destination);

            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(
                new[] { "001.webp", "002.webp", "ComicInfo.xml" },
                zip.Entries.Select(e => e.FullName)
            );
            Assert.Equal(new byte[] { 10, 11 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 12, 13 }, ReadEntry(zip, "002.webp"));
            Assert.Equal("<ComicInfo/>"u8.ToArray(), ReadEntry(zip, "ComicInfo.xml"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetOrCreateSession_ResetsCompletedPagesWhenIdentityChanges()
    {
        PageStreamSession session = _spool.GetOrCreateSession(2, "identity-a", 2);
        await _spool.WritePageAsync(
            session,
            0,
            new MemoryStream(new byte[] { 1 }),
            TestContext.Current.CancellationToken
        );
        Assert.Single(_spool.GetCompletedPages(session));

        PageStreamSession same = _spool.GetOrCreateSession(2, "identity-b", 2);

        Assert.Same(session, same);
        Assert.Empty(_spool.GetCompletedPages(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WritePage_IsIdempotent()
    {
        PageStreamSession session = _spool.GetOrCreateSession(3, "identity", 1);

        await _spool.WritePageAsync(
            session,
            0,
            new MemoryStream(new byte[] { 1, 2, 3 }),
            TestContext.Current.CancellationToken
        );
        await _spool.WritePageAsync(
            session,
            0,
            new MemoryStream(new byte[] { 9 }),
            TestContext.Current.CancellationToken
        );

        Assert.True(_spool.IsComplete(session));
        Assert.Single(_spool.GetCompletedPages(session));
        using Stream page = File.OpenRead(session.PagePath(0));
        Assert.Equal(new byte[] { 9 }, ReadAll(page));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkPageFailed_CountsAsCompleteAndCopiesTheSourceThrough()
    {
        string directory = Directory.CreateTempSubdirectory("spool_failed").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            var pages = new List<SpoolPageDescriptor>
            {
                new(0, "001.jpg", "001.webp"),
                new(1, "002.jpg", "002.webp"),
            };
            PageStreamSession session = _spool.GetOrCreateSession(5, "identity", pages.Count);

            await _spool.WritePageAsync(
                session,
                0,
                new MemoryStream(new byte[] { 10, 11 }),
                TestContext.Current.CancellationToken
            );
            _spool.MarkPageFailed(session, 1);

            Assert.True(_spool.IsComplete(session));

            string destination = Path.Combine(directory, "failed.cbz");
            _spool.Assemble(session, source, pages, destination);

            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 10, 11 }, ReadEntry(zip, "001.webp"));
            // The failed page is copied through unchanged under its source name.
            Assert.Equal(new byte[] { 4, 5, 6 }, ReadEntry(zip, "002.jpg"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Remove_DeletesSpoolDirectory()
    {
        PageStreamSession session = _spool.GetOrCreateSession(4, "identity", 1);
        Directory.CreateDirectory(session.Directory);
        File.WriteAllBytes(session.PagePath(0), new byte[] { 1 });

        _spool.Remove(4);

        Assert.False(Directory.Exists(session.Directory));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
