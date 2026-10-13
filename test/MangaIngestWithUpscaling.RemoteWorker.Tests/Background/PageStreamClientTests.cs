using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Drives the real <see cref="PageStreamClient"/> against a stubbed gRPC server (a substituted
/// generated client) and a fake local worker, so the fetch/upload/resume wiring is exercised
/// without a real server, database or upscaler.
/// </summary>
public class PageStreamClientTests
{
    private static readonly UpscalerProfile Profile = new()
    {
        Name = "test",
        CompressionFormat = SharedCompressionFormat.Webp,
        ScalingFactor = SharedScaleFactor.TwoX,
        Quality = 80,
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_StreamsAndAssemblesTheChapter()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Equal(new[] { 0, 1 }, server.RequestedPages);
            Assert.True(File.Exists(destination));

            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(
                new[] { "001.webp", "002.webp", "ComicInfo.xml" },
                zip.Entries.Select(e => e.FullName)
            );
            Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
            Assert.Equal("<ComicInfo/>"u8.ToArray(), ReadEntry(zip, "ComicInfo.xml"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_UsesTheServersPreprocessingOptions()
    {
        // The server owns the preprocessing settings, so the worker must apply the options the
        // manifest carries, not its own (here: disabled) configuration.
        string directory = Directory.CreateTempSubdirectory("page_stream_preprocess").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination)
            {
                PreprocessingJson = JsonSerializer.Serialize(
                    new ImagePreprocessingOptions { MaxDimension = 1234 },
                    WorkerJsonContext.Default.ImagePreprocessingOptions
                ),
            };
            var client = server.CreateClient();
            var resize = Substitute.For<IImageResizeService>();
            var sut = CreateClient(
                new FakeWorkerClient(),
                CreateScopeFactory(resize),
                new UpscalerConfig { ImageFormatConversionRules = [] }
            );

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            await resize
                .Received(server.Pages.Count)
                .PreprocessImageInPlaceAsync(
                    Arg.Any<string>(),
                    Arg.Is<ImagePreprocessingOptions>(o => o.MaxDimension == 1234),
                    Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WithAWindowsIllegalSourceName_StreamsIt()
    {
        // The server preserves legal-on-Linux names like "a:b.jpg"; the worker must not build a path
        // that Windows forbids (the page index alone names the local file).
        string directory = Directory.CreateTempSubdirectory("page_stream_illegal").FullName;
        try
        {
            string source = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "a:b.jpg", new byte[] { 1, 2, 3 });
            }

            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 0 }, server.RequestedPages);
            Assert.True(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_MultiChunkPage_ReassemblesEveryByte()
    {
        // A page larger than the 1 MiB upload chunk size (and the fetch chunk size) must survive the
        // multi-chunk download and upload without truncation or reordering.
        string directory = Directory.CreateTempSubdirectory("page_stream_multichunk").FullName;
        try
        {
            byte[] payload = new byte[(1024 * 1024) + 4096];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 251);
            }

            string source = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "001.jpg", payload);
            }

            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            // The fake worker "upscales" by reversing the bytes.
            Assert.Equal(payload.Reverse().ToArray(), server.Uploaded[0]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WithTheRealWorkerClient_UploadsEveryUpscaledPage()
    {
        // Regression: PageStreamClient keys a finished page by the worker page name, so
        // MangaJaNaiWorkerClient must report ChapterPage.Name as UpscaleJobFile.Input. Reporting the
        // local temp path instead left every page unmatched, uploaded nothing, and failed the
        // chapter. This drives the real producer (not FakeWorkerClient) so that contract is pinned.
        string directory = Directory.CreateTempSubdirectory("page_stream_real_worker").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();

            // No GPU/model needed: "upscale" by reversing the fetched source bytes into the staged
            // output path, exactly as FakeWorkerClient does, so the upload carries upscaled bytes.
            var engine = Substitute.For<IOnnxUpscaleEngine>();
            engine
                .UpscaleFileStagedAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<SharedCompressionFormat>(),
                    Arg.Any<int?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci =>
                    Task.FromResult<Task>(
                        ReverseIntoAsync(ci.ArgAt<string>(0), ci.ArgAt<string>(1))
                    )
                );

            var worker = new MangaJaNaiWorkerClient(
                engine,
                Substitute.For<IOnnxSessionFactory>(),
                NullLogger<MangaJaNaiWorkerClient>.Instance
            );
            var sut = CreateClient(worker);

            // A name/path mismatch makes this throw InvalidOperationException("Page upscaling failed").
            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 0, 1 }, server.RequestedPages);
            Assert.True(File.Exists(destination));

            using ZipArchive zip = ZipFile.OpenRead(destination);
            // The assembled chapter must hold the upscaled pixels, not the fetched source (1,2,3 /
            // 4,5,6), and each page must map back to its own index.
            Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task ReverseIntoAsync(string inputPath, string outputPath)
    {
        byte[] source = await File.ReadAllBytesAsync(inputPath);
        await File.WriteAllBytesAsync(outputPath, source.Reverse().ToArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ResumesAtTheFirstMissingPageAfterADrop()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_resume").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();

            // First run: the worker drops after the first page, which has already been uploaded.
            var droppingWorker = new FakeWorkerClient { DropAfterPages = 1 };
            var sut = CreateClient(droppingWorker);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
            Assert.Equal(new[] { 0 }, server.RequestedPages);
            Assert.Contains(0, server.Completed);
            Assert.False(File.Exists(destination));

            // Second run: only the missing page is fetched and the chapter is assembled.
            server.RequestedPages.Clear();
            var recoveringWorker = new FakeWorkerClient();
            var recovering = CreateClient(recoveringWorker);
            await recovering.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Equal(new[] { 1 }, server.RequestedPages);
            Assert.True(File.Exists(destination));
            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReturnsImmediatelyWhenTheServerAlreadyCompletedTheChapter()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_done").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.Completed.Add(0);
            server.Completed.Add(1);
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Empty(server.RequestedPages);
            Assert.Equal(0, worker.ProcessedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReturnsCleanlyWhenTheCompleteManifestOmitsPages()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_complete").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { OmitPagesWhenComplete = true };
            server.Completed.UnionWith(server.Pages.Select(p => p.Index));
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            // The real server's complete response carries no descriptors; the client must return
            // before the "no pages" guard, not throw.
            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Empty(server.RequestedPages);
            Assert.Equal(0, worker.ProcessedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunDetectionAsync_ReturnsCleanlyWhenTheCompleteManifestOmitsPages()
    {
        string directory = Directory.CreateTempSubdirectory("page_detect_complete").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { OmitPagesWhenComplete = true };
            server.Completed.UnionWith(server.Pages.Select(p => p.Index));
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunDetectionAsync(client, 1, TestContext.Current.CancellationToken);

            Assert.Empty(server.RequestedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_KeepsSameStemmedPagesInDifferentFoldersDistinct()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_nested").FullName;
        try
        {
            string source = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "ch1/001.jpg", new byte[] { 1, 2, 3 });
                WriteEntry(zip, "ch2/001.jpg", new byte[] { 4, 5, 6 });
            }

            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            // Both pages share the stem "001" and the local worker reverses each page's bytes. A
            // flattened local output name would upload the same (last-written) file for both.
            Assert.Equal(new byte[] { 3, 2, 1 }, server.Uploaded[0]);
            Assert.Equal(new byte[] { 6, 5, 4 }, server.Uploaded[1]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_FailsTheChapterWhenAPageCouldNotBeUpscaled()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_error").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient { PageStatus = UpscaleJobStatus.Error });

            // A page the engine could not upscale must not be passed through as if it were the page:
            // the whole-CBZ path fails the chapter and the streamed path now agrees, so a chapter is
            // never committed as upscaled with a stale page in it.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_FailsWhenTheServerOmitsARequestedPage()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_omit").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.OmitFromFetch.Add(1);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            // The server silently skips a page it cannot find; the client must fail the chapter
            // rather than let it hang in Processing.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_FailsWhenTheServerSubstitutesAnExtraManifestPageForARequestedOne()
    {
        // A server that drops one requested page but sends a different manifest page in its place
        // makes yielded.Count equal missingPages.Count, so a count-based guard would accept the
        // fetch and let the chapter finalize incomplete. The absent set must be computed directly.
        string directory = Directory.CreateTempSubdirectory("page_stream_swap").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            // Page 0 is already spooled, so the client requests [1, 2]; the server drops page 2 and
            // sends page 0 (a manifest page it was not asked for) instead.
            server.Completed.Add(0);
            server.OmitFromFetch.Add(2);
            server.ExtraFetchIndexes.Add(0);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );

            Assert.Contains("did not send page(s) 2", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_FailsWhenAChunkOfAnOpenPageArrivesTwice()
    {
        // A chunk repeated while its page is still open cannot be skipped safely: appending it again
        // would assemble the page from a byte stream the server never sent, and the upscaler would
        // commit that page as a successful result. The sequence guard must fail the fetch instead, so
        // the retry re-fetches rather than the chapter silently producing a corrupt source page. (The
        // duplicate of a *completed* page is a different shape — already whole and yielded — and is
        // still skipped, which RunAsync_ATerminatingChunkSentTwice_IsSkippedRatherThanCrashed pins.)
        string directory = Directory.CreateTempSubdirectory("page_stream_dup_chunk").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.RepeatFirstChunkForPage.Add(1);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );

            Assert.Contains("out of sequence", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_StopsTheChapterWhenAnUploadIsRejected()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_reject").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 100);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.FailUploadForPage.Add(0);
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );

            // The upload loop's rejection must be surfaced (not masked by the consequential
            // cancellation) and must stop the local worker instead of upscaling the whole chapter.
            Assert.Contains("simulated rejection", error.Message);
            // Assert on the cancellation signal rather than a page count: a synchronous fake can race
            // through many tiny pages before the cancellation is observed.
            Assert.True(
                worker.Canceled,
                "Expected the upload rejection to cancel the local worker."
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ThrowsARestartExceptionForANonTerminalUploadRejection()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_restart").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 50);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.RestartUploadForPage.Add(0);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            // A non-terminal rejection means "restart the chapter", not "fail": the worker surfaces
            // a restart exception so the caller requeues without dropping the spool.
            await Assert.ThrowsAsync<PageStreamRestartException>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WithAnOpenFetchStream_SurfacesTheRestartOverTheCancelledFetch()
    {
        // Regression: an upload failure cancels the in-flight page fetch, and grpc-dotnet reports
        // that client-side cancellation as RpcException(StatusCode.Cancelled), not as an
        // OperationCanceledException. A type-only check treated the consequential cancellation as a
        // real chapter verdict and masked the non-terminal restart the server actually reported.
        // The stream is deliberately held open before the second page so the cancellation is
        // guaranteed to hit an in-flight read (the deterministic shape of the real race), rather
        // than the fetch finishing first by luck.
        string directory = Directory.CreateTempSubdirectory("page_stream_open_fetch").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { HoldFetchAfterFirstPage = true };
            server.RestartUploadForPage.Add(0);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            var error = await Assert.ThrowsAsync<PageStreamRestartException>(() =>
                sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );

            Assert.Contains("simulated restart", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_CapturesTheEngineIdentityForTheWholeStream()
    {
        // A backend change mid-stream (GetEffectiveBackend re-probes a resolution that came from a
        // transient probe failure) must not make the uploads carry a new engine identity. The server
        // records the identity the manifest established and rejects a page whose engine differs,
        // which restarts the chapter and discards every already-upscaled page. The stream must use
        // the captured identity throughout, while the next chapter must see the new one.
        string directory = Directory.CreateTempSubdirectory("page_stream_engine_capture").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var engineIdentity = new StubEngineIdentityProvider { Upscaler = "engine-A" };
            // Flip the reported backend the moment the first page's upload is processed: a second
            // read during the stream (the bug) would then carry "engine-B".
            server.OnUploadPage = _ => engineIdentity.Upscaler = "engine-B";
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient(), engineIdentity: engineIdentity);

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Equal("engine-A", server.ManifestEngineIdentity);
            Assert.NotEmpty(server.UploadEngineIdentities);
            Assert.All(server.UploadEngineIdentities, value => Assert.Equal("engine-A", value));

            // The next stream re-reads the provider, so a genuine backend change is not lost.
            var secondServer = new FakePageServer(source, Path.Combine(directory, "out2.cbz"));
            var secondSut = CreateClient(new FakeWorkerClient(), engineIdentity: engineIdentity);
            await secondSut.RunAsync(
                secondServer.CreateClient(),
                1,
                Profile,
                TestContext.Current.CancellationToken
            );

            Assert.Equal("engine-B", secondServer.ManifestEngineIdentity);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WithAnUnexpectedFetchIndex_ThrowsARestartException()
    {
        // The server streams only the requested manifest indexes; an index it never described is a
        // protocol slip. The manifest lookup used to throw KeyNotFoundException, which fell through
        // the classifier to Permanent and made the server drop the spool of already-upscaled pages.
        // It must be a restart so those pages survive.
        string directory = Directory.CreateTempSubdirectory("page_stream_unknown_index").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { UnknownFetchIndex = 99 };
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            var error = await Assert.ThrowsAsync<PageStreamRestartException>(() =>
                sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken)
            );

            Assert.Contains("not in the manifest", error.Message);
            Assert.Equal(
                PageStreamDisposition.Retry,
                RemoteTaskProcessor.ClassifyStreamingDisposition(error)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ThrowsWhenASourcePageIsEmpty()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_empty").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.EmptyFetchForPage.Add(0);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            // A 0-byte source entry must fail loudly instead of being treated as a fetched page: the
            // detection path would "inspect" an empty file, swallow the decode failure and finalize
            // the chapter without ever looking at the page.
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
            Assert.Contains("with no data", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ATerminatingChunkSentTwice_IsSkippedRatherThanCrashed()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_duplicate").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 3);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.DuplicateLastChunkForPage.Add(0);
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            // This server never sends a duplicate (GetPages iterates distinct indexes), but a
            // protocol slip has to be skipped, not turned into a NullReferenceException out of
            // the page writer: the duplicate arrives after its IsLast chunk already closed the
            // file, and the NRE escalated as a permanent failure dropped the spool of
            // already-fetched pages. The duplicate is skipped; every page still completes.
            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Equal(3, worker.ProcessedPages);
            Assert.True(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_SurfacesATerminalUploadFailureOverATransientChapterCrash()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_precedence").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, pageCount: 50);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.FailUploadForPage.Add(0);
            var client = server.CreateClient();
            // The worker fails right after page 0 (an infrastructure-grade failure) while that
            // page's upload is rejected terminally.
            var worker = new FakeWorkerClient
            {
                DropAfterPages = 1,
                DropException = new IOException("simulated crash"),
            };
            var sut = CreateClient(worker);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );

            // The permanent upload rejection must win over the (transient) crash, or the task would
            // requeue forever with the spool intact instead of being reported.
            Assert.Contains("simulated rejection", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_PausesTheWorkerWhenTheUploadBacklogIsFull()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_backlog").FullName;
        try
        {
            const int pageCount = 20;
            string source = CreateManyPageSourceCbz(directory, pageCount);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { BlockUploads = true };
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            Task run = sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            // The first upload is held in flight, so the worker must fill the bounded backlog and then
            // block rather than racing through the whole chapter.
            await server.FirstUploadStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );
            await WaitUntilAsync(
                () => worker.ProcessedPages >= 8,
                TestContext.Current.CancellationToken
            );
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.True(
                worker.ProcessedPages <= 8,
                $"Expected at most 8 pages finished while uploads are blocked, saw {worker.ProcessedPages}."
            );

            server.ReleaseUploads.TrySetResult();
            await run;

            Assert.Equal(pageCount, worker.ProcessedPages);
            Assert.True(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunDetectionAsync_ResumesWithoutReDetectingCompletedPagesAfterADrop()
    {
        string directory = Directory.CreateTempSubdirectory("page_detect_resume").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination)
            {
                ManifestTaskType = TaskType.SplitDetection,
            };
            var client = server.CreateClient();

            int detectionCalls = 0;
            var detection = Substitute.For<ISplitDetectionService>();
            detection
                .DetectSplitsAsync(
                    Arg.Any<string>(),
                    Arg.Any<IProgress<UpscaleProgress>?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci =>
                {
                    detectionCalls++;
                    if (detectionCalls == 2)
                    {
                        throw new InvalidOperationException("simulated detection drop");
                    }

                    return new List<SplitDetectionResult>
                    {
                        new() { ImagePath = ci.Arg<string>(), Count = 0 },
                    };
                });

            var sut = CreateClient(new FakeWorkerClient(), CreateDetectionScopeFactory(detection));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                sut.RunDetectionAsync(client, 1, TestContext.Current.CancellationToken)
            );

            // Page 0 was detected and uploaded before the drop; page 1 was not.
            Assert.Contains(0, server.Completed);
            Assert.DoesNotContain(1, server.Completed);

            // The retry resumes at the missing page instead of re-detecting the completed one.
            server.RequestedPages.Clear();
            var recovering = CreateClient(
                new FakeWorkerClient(),
                CreateDetectionScopeFactory(detection)
            );
            await recovering.RunDetectionAsync(client, 1, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 1 }, server.RequestedPages);
            Assert.Contains(1, server.Completed);
            Assert.Equal(3, detectionCalls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private const string EngineIdentityValue = "test-engine";

    private sealed class StubEngineIdentityProvider : IEngineIdentityProvider
    {
        public string Upscaler { get; set; } = EngineIdentityValue;
        public string Detector => EngineIdentityValue;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_PreprocessesFetchedPagesWhenEnabled()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_preprocess").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var resize = Substitute.For<IImageResizeService>();
            var sut = CreateClient(
                new FakeWorkerClient(),
                CreateScopeFactory(resize),
                new UpscalerConfig()
            );

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            // Both fetched pages must be preprocessed so a streamed chapter matches the whole-CBZ
            // path, which preprocesses the archive before upscaling.
            await resize
                .Received(2)
                .PreprocessImageInPlaceAsync(
                    Arg.Any<string>(),
                    Arg.Any<ImagePreprocessingOptions>(),
                    Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WhenPreprocessingConvertsTheFormat_UsesTheConvertedPathForThePage()
    {
        // Regression: PreprocessImageInPlaceAsync returns the path of the preprocessed page, and when
        // a format conversion changes the extension it moves the converted file next to the original
        // under the CONVERTED name and deletes the original. FetchPagesAsync used to discard that
        // return value and yield the original path, so with a conversion in effect (the default
        // PNG -> JPG rules) the engine was handed a path to a file preprocessing had already deleted.
        // Every converted page failed with FileNotFoundException and the chapter could never stream.
        string directory = Directory.CreateTempSubdirectory("page_stream_conversion").FullName;
        try
        {
            // A .png source page: the default nets a PNG -> JPG conversion whenever the extension
            // changes, which is exactly the case that used to break.
            string source = Path.Combine(directory, "source.cbz");
            using (ZipArchive zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "001.png", new byte[] { 1, 2, 3 });
            }

            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();

            // Mirror the real service's contract: honour the conversion rule carried in the options,
            // write the converted file under its new name, delete the original, and report the new
            // path. Driving this off the received options also pins that the default PNG -> JPG rule
            // is what reached the service.
            byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20, 0x30];
            var resize = Substitute.For<IImageResizeService>();
            resize
                .PreprocessImageInPlaceAsync(
                    Arg.Any<string>(),
                    Arg.Any<ImagePreprocessingOptions>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci =>
                {
                    string input = ci.ArgAt<string>(0);
                    ImagePreprocessingOptions options = ci.ArgAt<ImagePreprocessingOptions>(1);
                    string extension = Path.GetExtension(input).ToLowerInvariant();
                    ImageFormatConversionRule? rule = options.FormatConversionRules.FirstOrDefault(
                        r => r.FromFormat.Equals(extension, StringComparison.OrdinalIgnoreCase)
                    );
                    if (rule is null)
                    {
                        return Task.FromResult(input);
                    }

                    string converted = Path.Combine(
                        Path.GetDirectoryName(input)!,
                        Path.GetFileNameWithoutExtension(input)
                            + (rule.ToFormat.StartsWith('.') ? rule.ToFormat : "." + rule.ToFormat)
                    );
                    File.WriteAllBytes(converted, jpegBytes);
                    File.Delete(input);
                    return Task.FromResult(converted);
                });

            var sut = CreateClient(worker, CreateScopeFactory(resize), new UpscalerConfig());

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            ChapterPage page = Assert.Single(worker.SeenPages);
            // The index key is the worker page name the engine echoes back via OnPageDone, so it must
            // stay the pre-conversion name even though the on-disk file was renamed to .jpg.
            Assert.Equal("00000.png", page.Name);
            Assert.EndsWith(".jpg", page.Path);
            Assert.NotEqual(Path.ChangeExtension(page.Path, ".png"), page.Path);

            // The bytes read at that path are the converted JPEG, not the deleted original.
            byte[] bytes = worker.SeenPageBytes[0];
            Assert.Equal(jpegBytes, bytes);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xD8, bytes[1]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(20_000_000L, 20.0)] // 20 MP × 1 min base exceeds the 15 min floor
    [InlineData(4_000_000L, 15.0)] // 4 MP × 1 min is under the floor, so the floor applies
    public async Task RunAsync_ScalesTheInactivityTimeoutByTheLargestPage(
        long maxPagePixels,
        double expectedMinutes
    )
    {
        // The server owns the source's largest page and reports it in the manifest, so the worker
        // can scale its inactivity kill the same way the whole-CBZ path does. Pinning the value the
        // worker hands the local chapter runner keeps the two paths from drifting apart.
        string directory = Directory.CreateTempSubdirectory("page_stream_timeout").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { MaxPagePixels = maxPagePixels };
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(
                worker,
                config: new UpscalerConfig
                {
                    ImageFormatConversionRules = [],
                    UpscaleTimeout = TimeSpan.FromMinutes(1),
                }
            );

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.NotNull(worker.LastTimeout);
            Assert.Equal(expectedMinutes, worker.LastTimeout!.Value.TotalMinutes, precision: 2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_DisablesTheInactivityTimeoutWhenConfiguredNonPositive()
    {
        // A non-positive UpscaleTimeout disables the inactivity kill on both paths, so the worker
        // must pass no timeout at all rather than a zero/negative one.
        string directory = Directory.CreateTempSubdirectory("page_stream_notimeout").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { MaxPagePixels = 20_000_000 };
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(
                worker,
                config: new UpscalerConfig
                {
                    ImageFormatConversionRules = [],
                    UpscaleTimeout = TimeSpan.Zero,
                }
            );

            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Null(worker.LastTimeout);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static PageStreamClient CreateClient(
        IMangaJaNaiWorkerClient worker,
        IServiceScopeFactory? scopeFactory = null,
        UpscalerConfig? config = null,
        IEngineIdentityProvider? engineIdentity = null
    ) =>
        new(
            worker,
            scopeFactory ?? CreateScopeFactory(),
            Options.Create(config ?? new UpscalerConfig { ImageFormatConversionRules = [] }),
            engineIdentity ?? new StubEngineIdentityProvider(),
            Substitute.For<ILogger<PageStreamClient>>()
        );

    private static IServiceScopeFactory CreateScopeFactory(IImageResizeService? resize = null)
    {
        resize ??= Substitute.For<IImageResizeService>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IImageResizeService)).Returns(resize);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private static IServiceScopeFactory CreateDetectionScopeFactory(
        ISplitDetectionService detection
    )
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISplitDetectionService)).Returns(detection);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_WithMoreFailedPagesThanTheUploadBacklog_DoesNotWedge()
    {
        // The page-done callback acquires a bounded upload slot (UploadBacklogLimit = 8) and the
        // upload loop is what releases it. A failed page that hands nothing to the loop and releases
        // nothing burns its slot for good, so the ninth failure would block the callback — and the
        // wait token is linked only to shutdown, so the worker would never process another task. More
        // pages than the backlog is what makes that visible: this must fail the chapter, not hang.
        string directory = Directory.CreateTempSubdirectory("page_stream_slots").FullName;
        try
        {
            string source = CreateManyPageSourceCbz(directory, 12);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient { PageStatus = UpscaleJobStatus.Error });

            Task run = sut.RunAsync(client, 1, Profile, CancellationToken.None);

            // A wedge surfaces as this never completing, which the timeout turns into a failure.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateSourceCbz(string directory)
    {
        string path = Path.Combine(directory, "source.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
        WriteEntry(zip, "002.jpg", new byte[] { 4, 5, 6 });
        WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
        return path;
    }

    private static string CreateManyPageSourceCbz(string directory, int pageCount)
    {
        string path = Path.Combine(directory, "source.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (int i = 1; i <= pageCount; i++)
        {
            WriteEntry(zip, $"{i:D3}.jpg", new byte[] { 1, 2, 3 });
        }

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

    /// <summary>Stubbed gRPC server: manifest, page fetch, and per-page upload with assembly.</summary>
    private sealed class FakePageServer
    {
        private const string Identity = "identity";
        private readonly string _sourcePath;
        private readonly string _destinationPath;

        public FakePageServer(string sourcePath, string destinationPath)
        {
            _sourcePath = sourcePath;
            _destinationPath = destinationPath;
            using ZipArchive zip = ZipFile.OpenRead(sourcePath);
            int index = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
                {
                    continue;
                }

                string stem = Path.GetFileNameWithoutExtension(entry.FullName);
                Pages.Add(
                    new PageDescriptor
                    {
                        Index = index,
                        SourceName = entry.FullName,
                        OutputName = $"{stem}.webp",
                    }
                );
                index++;
            }
        }

        public List<PageDescriptor> Pages { get; } = new();
        public HashSet<int> Completed { get; } = new();
        public List<int> RequestedPages { get; } = new();
        public HashSet<int> OmitFromFetch { get; } = new();
        public HashSet<int> FailUploadForPage { get; } = new();
        public HashSet<int> RestartUploadForPage { get; } = new();

        /// <summary>
        /// Manifest indexes streamed even though the client did not request them — a protocol slip
        /// this server never makes (GetPages echoes only the requested indexes), reproduced to pin
        /// the completeness guard against a count-based comparison.
        /// </summary>
        public HashSet<int> ExtraFetchIndexes { get; } = new();

        /// <summary>
        /// A page index the manifest never described, streamed before the requested pages — a protocol
        /// slip the real server cannot produce. Reproduces the client-side manifest lookup.
        /// </summary>
        public int? UnknownFetchIndex { get; set; }

        /// <summary>The engine identity the client sent on the manifest request.</summary>
        public string? ManifestEngineIdentity { get; private set; }

        /// <summary>The engine identity carried by each upload the client sent, in order.</summary>
        public List<string> UploadEngineIdentities { get; } = new();

        /// <summary>Invoked with the page index of each upload, before the response is produced.</summary>
        public Action<int>? OnUploadPage { get; set; }

        /// <summary>Largest page (pixels) the manifest reports, for the worker's timeout scaling.</summary>
        public long MaxPagePixels { get; set; }

        /// <summary>Pages whose fetch yields only the terminating chunk (a 0-byte source entry).</summary>
        public HashSet<int> EmptyFetchForPage { get; } = new();

        /// <summary>
        /// Pages whose terminating chunk is sent twice, after the file it closed — a protocol slip
        /// this server never makes (GetPages iterates distinct indexes), reproduced for the guard
        /// against it.
        /// </summary>
        public HashSet<int> DuplicateLastChunkForPage { get; } = new();

        /// <summary>
        /// Pages whose first data chunk is sent twice while the page is still open — the shape that
        /// would append the same bytes to a page that is not finished. Unlike the duplicate-after-
        /// completion case this cannot be skipped safely: the page would be assembled from a byte
        /// stream the server never sent, and the upscaler would commit it as a result.
        /// </summary>
        public HashSet<int> RepeatFirstChunkForPage { get; } = new();
        public Dictionary<int, byte[]> Uploaded { get; } = new();
        public bool OmitPagesWhenComplete { get; set; }
        public string PreprocessingJson { get; set; } = string.Empty;
        public TaskType ManifestTaskType { get; set; } = TaskType.Upscale;

        /// <summary>Hold every upload inside <c>CompleteAsync</c> until <see cref="ReleaseUploads" />.</summary>
        public bool BlockUploads { get; set; }
        public TaskCompletionSource FirstUploadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUploads { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// When set, the page stream holds open after the first page's terminating chunk until
        /// <see cref="ReleaseFetch" />, so a test can deterministically race an upload failure against
        /// an in-flight fetch (the stream is still open when the upload fails, and the cancellation
        /// therefore surfaces from the stream reader rather than at a page boundary).
        /// </summary>
        public bool HoldFetchAfterFirstPage { get; set; }

        /// <summary>Set once the stream is parked in the hold, so a test can order work against it.</summary>
        public TaskCompletionSource FetchHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases a stream held by <see cref="HoldFetchAfterFirstPage" />.</summary>
        public TaskCompletionSource ReleaseFetch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpscalingService.UpscalingServiceClient CreateClient()
        {
            var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
            client
                .GetPageManifestAsync(
                    Arg.Any<PageManifestRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci =>
                {
                    ManifestEngineIdentity = ci.Arg<PageManifestRequest>().EngineIdentity;
                    return Unary(BuildManifest());
                });
            client
                .GetPages(
                    Arg.Any<GetPagesRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci => ServerStream(GetChunks(ci.Arg<GetPagesRequest>())));
            client
                .UploadPage(Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
                .Returns(_ => ClientStream());
            client
                .UploadPageDetectionAsync(
                    Arg.Any<UploadPageDetectionRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci =>
                {
                    Completed.Add(ci.Arg<UploadPageDetectionRequest>().PageIndex);
                    return Unary(new UploadDetectionResultResponse { Success = true });
                });
            client
                .KeepAliveAsync(
                    Arg.Any<KeepAliveRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => Unary(new KeepAliveResponse { IsAlive = true }));
            return client;
        }

        private PageManifestResponse BuildManifest()
        {
            var response = new PageManifestResponse
            {
                TaskId = 1,
                TaskIdentity = Identity,
                TaskType = ManifestTaskType,
                Complete = Completed.Count >= Pages.Count,
                PreprocessingJson = PreprocessingJson,
                MaxPagePixels = MaxPagePixels,
            };
            // The real server omits the descriptors when it reports the chapter as already
            // complete; allow tests to reproduce that shape.
            if (!(OmitPagesWhenComplete && response.Complete))
            {
                response.Pages.AddRange(Pages);
            }
            response.CompletedPages.AddRange(Completed);
            return response;
        }

        private IEnumerable<PageChunk> GetChunks(GetPagesRequest request)
        {
            using ZipArchive zip = ZipFile.OpenRead(_sourcePath);
            Dictionary<string, ZipArchiveEntry> entries = zip.Entries.ToDictionary(e => e.FullName);

            // An index the manifest never described, sent first so the client hits it before any page
            // completes. A real server cannot produce this (GetPages iterates the requested indexes).
            if (UnknownFetchIndex is int unknown)
            {
                PageDescriptor page = Pages[0];
                using Stream stream = entries[page.SourceName].Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();
                yield return new PageChunk
                {
                    TaskId = 1,
                    PageIndex = unknown,
                    ChunkNumber = 0,
                    Chunk = ByteString.CopyFrom(bytes),
                };
                yield return new PageChunk
                {
                    TaskId = 1,
                    PageIndex = unknown,
                    ChunkNumber = 1,
                    IsLast = true,
                };
            }

            foreach (int index in request.PageIndexes)
            {
                RequestedPages.Add(index);
                if (OmitFromFetch.Contains(index))
                {
                    continue;
                }

                foreach (PageChunk chunk in PageChunks(index, entries))
                {
                    yield return chunk;
                }
            }

            // A manifest page the client never asked for. A real server cannot produce this
            // (GetPages iterates the requested indexes), but it is the shape that defeats a
            // count-based completeness check, so the guard is tested against it.
            foreach (int index in ExtraFetchIndexes)
            {
                foreach (PageChunk chunk in PageChunks(index, entries))
                {
                    yield return chunk;
                }
            }
        }

        private IEnumerable<PageChunk> PageChunks(
            int index,
            Dictionary<string, ZipArchiveEntry> entries
        )
        {
            PageDescriptor page = Pages.First(p => p.Index == index);
            using Stream stream = entries[page.SourceName].Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();

            int chunkNumber = 0;
            if (!EmptyFetchForPage.Contains(index))
            {
                bool first = true;
                for (int offset = 0; offset < bytes.Length; offset += 4096)
                {
                    int length = Math.Min(4096, bytes.Length - offset);
                    var chunk = new PageChunk
                    {
                        TaskId = 1,
                        PageIndex = index,
                        ChunkNumber = chunkNumber++,
                        Chunk = ByteString.CopyFrom(bytes, offset, length),
                    };
                    yield return chunk;

                    if (first && RepeatFirstChunkForPage.Contains(index))
                    {
                        yield return chunk;
                    }

                    first = false;
                }
            }

            yield return new PageChunk
            {
                TaskId = 1,
                PageIndex = index,
                ChunkNumber = chunkNumber,
                IsLast = true,
            };

            if (DuplicateLastChunkForPage.Contains(index))
            {
                yield return new PageChunk
                {
                    TaskId = 1,
                    PageIndex = index,
                    ChunkNumber = chunkNumber,
                    IsLast = true,
                };
            }
        }

        private AsyncClientStreamingCall<UploadPageChunk, UploadPageResponse> ClientStream()
        {
            var response = new TaskCompletionSource<UploadPageResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var writer = new FakeClientStreamWriter<UploadPageChunk>(async chunks =>
            {
                if (chunks.Count == 0)
                {
                    response.TrySetResult(new UploadPageResponse { Success = true });
                    return;
                }

                if (BlockUploads)
                {
                    FirstUploadStarted.TrySetResult();
                    await ReleaseUploads.Task;
                }

                int pageIndex = chunks[0].PageIndex;
                UploadEngineIdentities.Add(chunks[0].EngineIdentity);
                OnUploadPage?.Invoke(pageIndex);
                if (FailUploadForPage.Contains(pageIndex))
                {
                    response.TrySetResult(
                        new UploadPageResponse
                        {
                            Success = false,
                            Message = "simulated rejection",
                            Terminal = true,
                        }
                    );
                    return;
                }

                if (RestartUploadForPage.Contains(pageIndex))
                {
                    // With the stream held open, wait until the fetch is parked inside the read before
                    // reporting the rejection, so the cancellation it triggers is guaranteed to hit an
                    // in-flight read instead of racing a page boundary.
                    if (HoldFetchAfterFirstPage)
                    {
                        await FetchHeld.Task;
                    }

                    response.TrySetResult(
                        new UploadPageResponse
                        {
                            Success = false,
                            Message = "simulated restart",
                            Terminal = false,
                        }
                    );
                    return;
                }

                Uploaded[pageIndex] = chunks
                    .Where(c => !c.Chunk.IsEmpty)
                    .SelectMany(c => c.Chunk.ToByteArray())
                    .ToArray();
                Completed.Add(pageIndex);
                if (Completed.Count >= Pages.Count)
                {
                    Assemble();
                }

                response.TrySetResult(new UploadPageResponse { Success = true });
            });

            return new AsyncClientStreamingCall<UploadPageChunk, UploadPageResponse>(
                writer,
                response.Task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }

        private void Assemble()
        {
            using ZipArchive source = ZipFile.OpenRead(_sourcePath);
            using ZipArchive output = ZipFile.Open(_destinationPath, ZipArchiveMode.Create);
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                PageDescriptor? page = Pages.FirstOrDefault(p => p.SourceName == entry.FullName);
                if (page is not null && Uploaded.TryGetValue(page.Index, out byte[]? bytes))
                {
                    ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
                    using Stream target = outputEntry.Open();
                    target.Write(bytes);
                }
                else
                {
                    ZipArchiveEntry outputEntry = output.CreateEntry(entry.FullName);
                    using Stream input = entry.Open();
                    using Stream target = outputEntry.Open();
                    input.CopyTo(target);
                }
            }
        }

        private static AsyncUnaryCall<T> Unary<T>(T value) =>
            new(
                Task.FromResult(value),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );

        private AsyncServerStreamingCall<PageChunk> ServerStream(IEnumerable<PageChunk> chunks) =>
            new(
                new FakeAsyncStreamReader<PageChunk>(
                    chunks,
                    holdAfter: chunk => HoldFetchAfterFirstPage && chunk.IsLast,
                    hold: HoldFetchUntilReleased
                ),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );

        private async Task HoldFetchUntilReleased(CancellationToken cancellationToken)
        {
            FetchHeld.TrySetResult();
            await ReleaseFetch.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _items;
        private readonly Func<T, bool>? _holdAfter;
        private readonly Func<CancellationToken, Task>? _hold;
        private bool _holdBeforeNext;

        public FakeAsyncStreamReader(
            IEnumerable<T> items,
            Func<T, bool>? holdAfter = null,
            Func<CancellationToken, Task>? hold = null
        )
        {
            _items = items.GetEnumerator();
            _holdAfter = holdAfter;
            _hold = hold;
        }

        public T Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // The hold runs before pulling the next item, i.e. after the previous item (the page's
            // terminating chunk) has already been delivered, which is where a real stream sits while
            // an upload failure cancels the in-flight read.
            if (_holdBeforeNext)
            {
                _holdBeforeNext = false;
                try
                {
                    await _hold!(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // grpc-dotnet surfaces a client-side call cancellation from the stream as
                    // RpcException(StatusCode.Cancelled) ("Call canceled by the client"), not as an
                    // OperationCanceledException, so the fake reports the same shape.
                    throw new RpcException(
                        new Status(StatusCode.Cancelled, "Call canceled by the client.")
                    );
                }
            }

            if (!_items.MoveNext())
            {
                return false;
            }

            Current = _items.Current;
            _holdBeforeNext = _holdAfter?.Invoke(Current) ?? false;
            return true;
        }
    }

    private sealed class FakeClientStreamWriter<T> : IClientStreamWriter<T>
    {
        private readonly List<T> _items = new();
        private readonly Func<List<T>, Task> _onComplete;

        public FakeClientStreamWriter(Func<List<T>, Task> onComplete)
        {
            _onComplete = onComplete;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            _items.Add(message);
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<T>.WriteAsync(T message, CancellationToken cancellationToken)
        {
            // The real grpc-dotnet writer implements the token overload; mirror it so the test
            // exercises the same path instead of the default interface method that throws.
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => _onComplete(_items);
    }
}
