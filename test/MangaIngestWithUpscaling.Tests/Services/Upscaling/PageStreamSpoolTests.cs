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
    public void TryCommitPage_DropsAPageWhoseTempFileWasDiscardedByAReset()
    {
        PageStreamSession session = _spool.GetOrCreateSession(103, "identity-a", "engine", 1);
        FileStream output = _spool.BeginPageWrite(session, 0, out string temp);
        output.Dispose();

        // Resetting to another identity deletes the directory holding the temp file; resetting back to
        // this identity makes the identity checks pass again, so the page must be dropped explicitly
        // rather than letting File.Move throw out of the commit.
        _spool.GetOrCreateSession(103, "identity-b", "engine", 1);
        _spool.GetOrCreateSession(103, "identity-a", "engine", 1);

        Assert.Equal(
            CommitPageResult.IdentityMismatch,
            _spool.TryCommitPage(session, "identity-a", "engine", 0, temp)
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
            _spool.Assemble(session, session.Identity, path, pages, destination);

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
            _spool.Assemble(session, session.Identity, path, pages, destination);

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
            _spool.Assemble(session, session.Identity, source, pages, destination);

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
    public async Task WritePageAsync_RemovesItsTempFileWhenTheCopyIsCancelled()
    {
        PageStreamSession session = _spool.GetOrCreateSession(310, "identity", "engine", 1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The copy throws TaskCanceledException, so match the cancellation base type.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _spool.WritePageAsync(
                session,
                0,
                new MemoryStream(new byte[] { 1, 2, 3 }),
                cancelled.Token
            )
        );

        // The page was never committed, so nothing may be left behind in the session directory.
        Assert.Empty(Directory.GetFiles(session.Directory));
        Assert.Empty(_spool.GetCompletedPages(session));
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
        Assert.True(_spool.TryReserveInFlight(session, 0, 100, out long staleGeneration));

        // The identity changes and resets the session (generation bumps, in-flight zeroed).
        _spool.GetOrCreateSession(40, "identity-b", "engine", 1);
        Assert.True(_spool.TryReserveInFlight(session, 0, 50, out _));

        // The stale release must not subtract from the new identity's in-flight accounting.
        _spool.ReleaseInFlight(session, staleGeneration, 100);
        Assert.Equal(50, session.InFlightByGeneration.Values.Single());
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
            _spool.AssemblePagesOnly(session, session.Identity, pages, destination);
            // A re-finalize into the same prepared repair target must not throw.
            _spool.AssemblePagesOnly(session, session.Identity, pages, destination);

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
                _spool.Assemble(
                    session,
                    session.Identity,
                    path,
                    pages,
                    Path.Combine(directory, "out.cbz")
                )
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

    [Fact]
    [Trait("Category", "Unit")]
    public void SweepStale_RemovesAnIdleSessionAndKeepsAFreshOne()
    {
        PageStreamSession stale = _spool.GetOrCreateSession(60, "identity", "engine", 1);
        string temp;
        using (FileStream page = _spool.BeginPageWrite(stale, 0, out temp))
        {
            page.Write(new byte[] { 1 });
        }

        _spool.CommitPage(stale, 0, temp);
        // Age the session past the retention window.
        stale.LastTouchedUtc = DateTime.UtcNow - TimeSpan.FromHours(48);

        PageStreamSession fresh = _spool.GetOrCreateSession(61, "identity", "engine", 1);
        Directory.CreateDirectory(fresh.Directory);

        _spool.SweepStale(TimeSpan.FromHours(24));

        Assert.Null(_spool.TryGetSession(60));
        Assert.False(Directory.Exists(stale.Directory));
        // A live session must survive the sweep.
        Assert.Same(fresh, _spool.TryGetSession(61));
        Assert.True(Directory.Exists(fresh.Directory));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageSpoolSweepService_SweepsStaleSessionsOnStartup()
    {
        var spool = new PageStreamSpool(Substitute.For<ILogger<PageStreamSpool>>());
        var cache = new PageContextCache();
        PageStreamSession session = spool.GetOrCreateSession(70, "identity", "engine", 1);
        session.LastTouchedUtc = DateTime.UtcNow - TimeSpan.FromDays(2);

        var service = new PageSpoolSweepService(
            spool,
            cache,
            Substitute.For<ILogger<PageSpoolSweepService>>()
        );

        // The startup sweep runs on the first continuation after startup rather than inline in StartAsync
        // (which would delay the host coming up), so a stale session left by a previous process is still
        // removed promptly instead of an hour later.
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The startup sweep may run on the background task rather than inline; wait briefly for
            // it so the test is not order-dependent.
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (spool.TryGetSession(70) is not null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Null(spool.TryGetSession(70));
            Assert.False(Directory.Exists(session.Directory));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryReserveInFlight_RejectsConcurrentReservationsThatExceedTheBudget()
    {
        PageStreamSession session = _spool.GetOrCreateSession(62, "identity", "engine", 2);

        // Neither page is committed yet, so both reservations must count against the budget.
        Assert.True(_spool.TryReserveInFlight(session, 0, PageStreamSpool.MaxTaskBytes - 1, out _));
        Assert.False(_spool.TryReserveInFlight(session, 1, 2, out _));

        // Releasing the first reservation frees the budget for the second.
        _spool.ReleaseInFlight(session, session.Generation, PageStreamSpool.MaxTaskBytes - 1);
        Assert.True(_spool.TryReserveInFlight(session, 1, 2, out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetOrCreateSession_ReplacesAFinalizedSession()
    {
        PageStreamSession first = _spool.GetOrCreateSession(80, "identity", "engine", 1);
        // A finalized session's directory is gone; a later manifest must get a usable session, not
        // one whose BeginPageWrite would recreate the deleted directory.
        _spool.Remove(80);
        Assert.True(first.Finalized);

        PageStreamSession second = _spool.GetOrCreateSession(80, "identity", "engine", 1);

        Assert.NotSame(first, second);
        Assert.False(second.Finalized);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Assemble_ThrowsARestartExceptionWhenTheIdentityChanged()
    {
        string directory = Directory.CreateTempSubdirectory("spool_identity").FullName;
        try
        {
            string path = CreateSourceCbz(directory);
            var pages = new List<SpoolPageDescriptor>
            {
                new(0, "001.jpg", "001.webp"),
                new(1, "002.jpg", "002.webp"),
            };
            PageStreamSession session = _spool.GetOrCreateSession(90, "identity", "engine", 2);
            string temp;
            using (FileStream page = _spool.BeginPageWrite(session, 0, out temp))
            {
                page.Write(new byte[] { 1 });
            }

            _spool.CommitPage(session, 0, temp);

            // TryBeginAssembly validated the identity earlier; a reset in between must be caught here
            // rather than mixing two identities' page bytes into one archive.
            PageStreamRestartException ex = Assert.Throws<PageStreamRestartException>(() =>
                _spool.Assemble(
                    session,
                    "a-different-identity",
                    path,
                    pages,
                    Path.Combine(directory, "out.cbz")
                )
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
    public void SweepStale_KeepsAnAssemblingSession()
    {
        PageStreamSession session = _spool.GetOrCreateSession(91, "identity", "engine", 1);
        session.LastTouchedUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        Assert.True(_spool.TryBeginAssembly(session, session.Identity));

        _spool.SweepStale(TimeSpan.FromHours(24));

        // An in-progress assembly must not be swept, even when it looks idle.
        Assert.Same(session, _spool.TryGetSession(91));
        Assert.False(session.Finalized);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BeginPageWrite_RejectsAFinalizedSession()
    {
        PageStreamSession session = _spool.GetOrCreateSession(95, "identity", "engine", 1);
        _spool.Remove(95);
        Assert.True(session.Finalized);

        // Refuse rather than recreate the deleted directory (which would leak an orphan dir).
        PageStreamRestartException ex = Assert.Throws<PageStreamRestartException>(() =>
            _spool.BeginPageWrite(session, 0, out _)
        );
        Assert.True(ex.ResetSpool);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetOrCreateSession_DoesNotResetASessionThatIsAssembling()
    {
        PageStreamSession session = _spool.GetOrCreateSession(100, "identity-a", "engine-a", 1);
        Assert.True(_spool.TryBeginAssembly(session, "identity-a"));

        // A competing manifest with a different content/engine identity must not reset (and delete)
        // the spool while the finalizer is running; it gets the in-progress session back instead, and
        // its worker restarts rather than corrupting the finalize.
        PageStreamSession competing = _spool.GetOrCreateSession(100, "identity-b", "engine-b", 1);

        Assert.Same(session, competing);
        Assert.Equal("identity-a", session.Identity);
        Assert.Equal("engine-a", session.EngineIdentity);

        // Once assembly ends, a reset is allowed again.
        _spool.EndAssembly(session);
        PageStreamSession reset = _spool.GetOrCreateSession(100, "identity-b", "engine-b", 1);

        Assert.Same(session, reset);
        Assert.Equal("identity-b", session.Identity);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsFinalized_IsTrueAfterRemove()
    {
        PageStreamSession session = _spool.GetOrCreateSession(101, "identity", "engine", 1);
        Assert.False(_spool.IsFinalized(session));

        _spool.Remove(101);

        Assert.True(_spool.IsFinalized(session));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("001.jpg", true)]
    [InlineData("ch/001.jpg", true)]
    [InlineData("ch\\001.jpg", true)]
    [InlineData("page1..jpg", true)]
    [InlineData("../evil.jpg", false)]
    [InlineData("..\\evil.jpg", false)]
    [InlineData(".. /evil.jpg", false)]
    [InlineData(".../evil.jpg", false)]
    [InlineData("C:/evil.jpg", false)]
    [InlineData("C:\\evil.jpg", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("\\evil.jpg", false)]
    public void IsSafeEntryName_RejectsTraversalAndWindowsForms(string name, bool expected)
    {
        Assert.Equal(expected, PageStreamSpool.IsSafeEntryName(name));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SweepStale_KeepsAnotherInstancesActiveRootAndReclaimsAnIdleOne()
    {
        // Session activity lives in memory, but the sweep reclaims a root by its filesystem timestamp, so
        // a chapter that is still streaming but whose pages were written a while ago used to look idle:
        // another instance sharing the temp directory could reclaim it mid-chapter.
        var active = new PageStreamSpool(Substitute.For<ILogger<PageStreamSpool>>());
        var idle = new PageStreamSpool(Substitute.For<ILogger<PageStreamSpool>>());
        var sweeper = new PageStreamSpool(Substitute.For<ILogger<PageStreamSpool>>());
        try
        {
            PageStreamSession activeSession = WriteOnePage(active, 300);
            PageStreamSession idleSession = WriteOnePage(idle, 301);

            // Nothing has changed on disk since these pages were written.
            AgeDirectory(active.SpoolRoot);
            AgeDirectory(activeSession.Directory);
            AgeDirectory(idle.SpoolRoot);
            AgeDirectory(idleSession.Directory);

            // A manifest refreshes the active spool's heartbeat without touching any page.
            active.GetOrCreateSession(300, "identity", "engine", 1);

            sweeper.SweepStale(TimeSpan.FromHours(24));

            Assert.True(
                Directory.Exists(active.SpoolRoot),
                "A spool root that is still being used must survive another instance's sweep."
            );
            Assert.False(
                Directory.Exists(idle.SpoolRoot),
                "An idle spool root is still reclaimed."
            );
        }
        finally
        {
            active.DeleteDirectory(active.SpoolRoot);
            sweeper.DeleteDirectory(sweeper.SpoolRoot);
        }
    }

    private static PageStreamSession WriteOnePage(PageStreamSpool spool, int taskId)
    {
        PageStreamSession session = spool.GetOrCreateSession(taskId, "identity", "engine", 1);
        string temp;
        using (FileStream page = spool.BeginPageWrite(session, 0, out temp))
        {
            page.WriteByte(1);
        }

        spool.CommitPage(session, 0, temp);
        return session;
    }

    private static void AgeDirectory(string directory) =>
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - TimeSpan.FromDays(2));

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
