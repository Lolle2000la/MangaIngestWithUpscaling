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
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task RunAsync_CopiesAPageTheEngineCouldNotProcessThroughUnchanged()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_error").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient { PageStatus = "error" });

            // Matching the whole-CBZ path, a page the engine cannot process is copied through
            // unchanged rather than failing the whole chapter.
            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.True(File.Exists(destination));
            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 1, 2, 3 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 4, 5, 6 }, ReadEntry(zip, "002.webp"));
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
            // The worker crashes right after page 0 while that page's upload is rejected terminally.
            var worker = new FakeWorkerClient
            {
                DropAfterPages = 1,
                DropException = new UpscaleWorkerCrashedException("simulated crash"),
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
        public string Upscaler => EngineIdentityValue;
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

    private static PageStreamClient CreateClient(
        FakeWorkerClient worker,
        IServiceScopeFactory? scopeFactory = null,
        UpscalerConfig? config = null
    ) =>
        new(
            worker,
            scopeFactory ?? CreateScopeFactory(),
            Options.Create(config ?? new UpscalerConfig { ImageFormatConversionRules = [] }),
            new StubEngineIdentityProvider(),
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

        /// <summary>Pages whose fetch yields only the terminating chunk (a 0-byte source entry).</summary>
        public HashSet<int> EmptyFetchForPage { get; } = new();
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
                .Returns(_ => Unary(BuildManifest()));
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
            foreach (int index in request.PageIndexes)
            {
                RequestedPages.Add(index);
                if (OmitFromFetch.Contains(index))
                {
                    continue;
                }

                PageDescriptor page = Pages.First(p => p.Index == index);
                using Stream stream = entries[page.SourceName].Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();

                int chunkNumber = 0;
                if (!EmptyFetchForPage.Contains(index))
                {
                    for (int offset = 0; offset < bytes.Length; offset += 4096)
                    {
                        int length = Math.Min(4096, bytes.Length - offset);
                        yield return new PageChunk
                        {
                            TaskId = 1,
                            PageIndex = index,
                            ChunkNumber = chunkNumber++,
                            Chunk = ByteString.CopyFrom(bytes, offset, length),
                        };
                    }
                }

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

        private static AsyncServerStreamingCall<PageChunk> ServerStream(
            IEnumerable<PageChunk> chunks
        ) =>
            new(
                new FakeAsyncStreamReader<PageChunk>(chunks),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
    }

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _items;

        public FakeAsyncStreamReader(IEnumerable<T> items)
        {
            _items = items.GetEnumerator();
        }

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_items.MoveNext())
            {
                Current = _items.Current;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
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
