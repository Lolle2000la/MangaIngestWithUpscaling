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
    public void TryCommitPage_RejectsAStaleIdentity()
    {
        PageStreamSession session = _spool.GetOrCreateSession(11, "old", "engine", 1);
        FileStream output = _spool.BeginPageWrite(session, 0, out string temp);
        output.Dispose();

        // A concurrent manifest changes the identity, resetting the shared session in place.
        _spool.GetOrCreateSession(11, "new", "engine", 1);

        Assert.Equal(
            CommitPageResult.IdentityMismatch,
            _spool.TryCommitPage(session, "old", "engine", 0, temp)
        );
        Assert.False(_spool.IsComplete(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryCommitPage_RejectsWhenOverBudget()
    {
        PageStreamSession session = _spool.GetOrCreateSession(13, "identity", "engine", 2);

        FileStream first = _spool.BeginPageWrite(session, 0, out string firstTemp);
        first.Dispose();
        Assert.Equal(
            CommitPageResult.Committed,
            _spool.TryCommitPage(
                session,
                "identity",
                "engine",
                0,
                firstTemp,
                PageStreamSpool.MaxTaskBytes
            )
        );

        FileStream second = _spool.BeginPageWrite(session, 1, out string secondTemp);
        second.Dispose();
        Assert.Equal(
            CommitPageResult.OverBudget,
            _spool.TryCommitPage(session, "identity", "engine", 1, secondTemp, 1)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryBeginAssembly_RejectsAfterRemoval()
    {
        PageStreamSession session = _spool.GetOrCreateSession(14, "identity", "engine", 1);
        Assert.True(_spool.TryBeginAssembly(session));

        _spool.Remove(14);
        _spool.EndAssembly(session);

        // Removal is terminal: a late upload must not re-run assembly on the deleted spool.
        Assert.False(_spool.TryBeginAssembly(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryBeginAssembly_RejectsAStaleIdentity()
    {
        PageStreamSession session = _spool.GetOrCreateSession(12, "old", "engine", 1);

        _spool.GetOrCreateSession(12, "new", "engine", 1);

        Assert.False(_spool.TryBeginAssembly(session, "old"));
        Assert.True(_spool.TryBeginAssembly(session, "new"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryBeginAssembly_IsExclusiveUntilEnded()
    {
        PageStreamSession session = _spool.GetOrCreateSession(9, "identity", "engine", 1);

        Assert.True(_spool.TryBeginAssembly(session));
        Assert.False(_spool.TryBeginAssembly(session));

        _spool.EndAssembly(session);

        Assert.True(_spool.TryBeginAssembly(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Assemble_PreservesNestedOutputPaths()
    {
        string directory = Directory.CreateTempSubdirectory("spool_nested").FullName;
        try
        {
            string path = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "ch1/001.jpg", new byte[] { 1, 2, 3 });
                WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
            }

            var pages = new List<SpoolPageDescriptor> { new(0, "ch1/001.jpg", "ch1/001.webp") };
            PageStreamSession session = _spool.GetOrCreateSession(
                4,
                "identity",
                "engine",
                pages.Count
            );
            string temp;
            using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
            {
                page.Write(new byte[] { 7, 8, 9 });
            }

            _spool.CommitPage(session, 0, temp);

            string destination = Path.Combine(directory, "out.cbz");
            _spool.Assemble(session, path, pages, destination);

            using ZipArchive output = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 7, 8, 9 }, ReadEntry(output, "ch1/001.webp"));
            Assert.NotNull(output.GetEntry("ComicInfo.xml"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Assemble_SkipsDuplicateSourceEntries()
    {
        string directory = Directory.CreateTempSubdirectory("spool_dup").FullName;
        try
        {
            string path = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
                WriteEntry(zip, "001.jpg", new byte[] { 9, 9, 9 }); // duplicate entry name
                WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
            }

            // BuildPageDescriptors keeps only the first of a repeated entry name.
            var pages = new List<SpoolPageDescriptor> { new(0, "001.jpg", "001.webp") };
            PageStreamSession session = _spool.GetOrCreateSession(20, "identity", "engine", 1);
            string temp;
            using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
            {
                page.Write(new byte[] { 7, 8, 9 });
            }

            _spool.CommitPage(session, 0, temp);

            string destination = Path.Combine(directory, "out.cbz");
            // The duplicate source entry must be skipped, not treated as an output-name collision.
            _spool.Assemble(session, path, pages, destination);

            using ZipArchive output = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 7, 8, 9 }, ReadEntry(output, "001.webp"));
            Assert.NotNull(output.GetEntry("ComicInfo.xml"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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
            PageStreamSession session = _spool.GetOrCreateSession(
                1,
                "identity",
                "engine",
                pages.Count
            );

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
        PageStreamSession session = _spool.GetOrCreateSession(2, "identity-a", "engine", 2);
        await _spool.WritePageAsync(
            session,
            0,
            new MemoryStream(new byte[] { 1 }),
            TestContext.Current.CancellationToken
        );
        Assert.Single(_spool.GetCompletedPages(session));

        PageStreamSession same = _spool.GetOrCreateSession(2, "identity-b", "engine", 2);

        Assert.Same(session, same);
        Assert.Empty(_spool.GetCompletedPages(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetOrCreateSession_ResetsWhenTheEngineChanges()
    {
        PageStreamSession session = _spool.GetOrCreateSession(30, "identity", "engine-a", 2);
        await _spool.WritePageAsync(
            session,
            0,
            new MemoryStream(new byte[] { 1 }),
            TestContext.Current.CancellationToken
        );
        Assert.Single(_spool.GetCompletedPages(session));

        PageStreamSession same = _spool.GetOrCreateSession(30, "identity", "engine-b", 2);

        // A different engine must discard the spool so pages from two engines are never mixed.
        Assert.Same(session, same);
        Assert.Empty(_spool.GetCompletedPages(session));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WritePage_IsIdempotent()
    {
        PageStreamSession session = _spool.GetOrCreateSession(3, "identity", "engine", 1);

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
    public void Remove_DeletesSpoolDirectory()
    {
        PageStreamSession session = _spool.GetOrCreateSession(4, "identity", "engine", 1);
        Directory.CreateDirectory(session.Directory);
        File.WriteAllBytes(session.PagePath(0), new byte[] { 1 });

        _spool.Remove(4);

        Assert.False(Directory.Exists(session.Directory));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReleaseInFlight_IgnoresAReservationFromAStaleGeneration()
    {
        PageStreamSession session = _spool.GetOrCreateSession(40, "identity-a", "engine", 1);
        Assert.True(_spool.TryReserveInFlight(session, 100, out long staleGeneration));

        // The identity changes and resets the session (generation bumps, in-flight zeroed).
        _spool.GetOrCreateSession(40, "identity-b", "engine", 1);
        Assert.True(_spool.TryReserveInFlight(session, 50, out _));

        // The stale release must not subtract from the new identity's in-flight accounting.
        _spool.ReleaseInFlight(session, staleGeneration, 100);
        Assert.Equal(50, session.InFlightBytes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AssemblePagesOnly_OverwritesAnExistingDestination()
    {
        string directory = Directory.CreateTempSubdirectory("spool_repair").FullName;
        try
        {
            var pages = new List<SpoolPageDescriptor> { new(0, "001.jpg", "001.webp") };
            PageStreamSession session = _spool.GetOrCreateSession(41, "identity", "engine", 1);
            string temp;
            using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
            {
                page.Write(new byte[] { 7, 8, 9 });
            }

            _spool.CommitPage(session, 0, temp);

            string destination = Path.Combine(directory, "missing.cbz");
            File.WriteAllBytes(destination, new byte[] { 1 });
            _spool.AssemblePagesOnly(session, pages, destination);
            // A re-finalize into the same prepared repair target must not throw.
            _spool.AssemblePagesOnly(session, pages, destination);

            using ZipArchive output = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 7, 8, 9 }, ReadEntry(output, "001.webp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Assemble_ThrowsARestartExceptionWhenTheSourceArchiveChanged()
    {
        string directory = Directory.CreateTempSubdirectory("spool_changed").FullName;
        try
        {
            string path = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
                WriteEntry(zip, "002.jpg", new byte[] { 4, 5, 6 });
            }

            // Descriptors only cover 001.jpg, so the archive no longer matches.
            var pages = new List<SpoolPageDescriptor> { new(0, "001.jpg", "001.webp") };
            PageStreamSession session = _spool.GetOrCreateSession(51, "identity", "engine", 1);
            string temp;
            using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
            {
                page.Write(new byte[] { 7, 8, 9 });
            }

            _spool.CommitPage(session, 0, temp);

            // Recoverable: the caller restarts the chapter instead of failing it terminally.
            PageStreamRestartException ex = Assert.Throws<PageStreamRestartException>(() =>
                _spool.Assemble(session, path, pages, Path.Combine(directory, "out.cbz"))
            );
            Assert.True(ex.ResetSpool);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ForgetMissingPages_DropsPagesWhoseFileIsGone()
    {
        PageStreamSession session = _spool.GetOrCreateSession(52, "identity", "engine", 2);
        string temp;
        using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
        {
            page.Write(new byte[] { 1 });
        }

        _spool.CommitPage(session, 0, temp);
        Assert.Single(_spool.GetCompletedPages(session));

        File.Delete(session.PagePath(0));
        _spool.ForgetMissingPages(session);

        Assert.Empty(_spool.GetCompletedPages(session));
        Assert.Equal(0, _spool.GetTotalBytes(session));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
