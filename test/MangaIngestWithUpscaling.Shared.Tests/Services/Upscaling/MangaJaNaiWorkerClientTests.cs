using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

/// <summary>
/// The streamed-chapter page pipeline's handling of engine exceptions that are not the page's
/// fault: these must leave the client unconverted, because converting them into a per-page error
/// escalates as a permanent chapter failure that drops the spool of already-upscaled pages — the
/// exact outcome <c>RemoteTaskProcessor.ClassifyStreamingFailure</c>'s Transient branch exists to
/// prevent.
/// </summary>
public class MangaJaNaiWorkerClientTests
{
    public static TheoryData<Func<Exception>, Type> RecoverableEngineFailures =>
        new()
        {
            // The engine's own comment for the all-NaN case relies on the transient classification
            // holding here ("a driver reset can produce it"), which it did not until the exception
            // was let out of the per-page catch.
            { () => new NonFiniteModelOutputException(), typeof(NonFiniteModelOutputException) },
            // A session/eviction race racing the page: the same class on the shutdown side.
            { () => new ObjectDisposedException("session"), typeof(ObjectDisposedException) },
        };

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(RecoverableEngineFailures))]
    public async Task RunChapterAsync_RecoversableEngineFailure_PropagatesInsteadOfFailingThePage(
        Func<Exception> failure,
        Type expectedType
    )
    {
        // The engine throws these for a mis-executing provider or a shutdown race, both of which
        // a freshly built session or a restart can change. Recorded as a per-page error they
        // ended the chapter permanently and dropped the already-upscaled pages' spool; left
        // unconverted they reach the worker's streaming classifier, which requeues the task with
        // the spool intact (bounded by the soft-failure cap).
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Task.FromException<Task>(failure()));

        var client = new MangaJaNaiWorkerClient(
            engine,
            Substitute.For<IOnnxSessionFactory>(),
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        string outputDir = Directory.CreateTempSubdirectory("worker_client").FullName;
        try
        {
            var request = new ChapterJobRequest
            {
                Id = "job",
                OutputFolder = outputDir,
                Format = CompressionFormat.Webp,
                Scale = ScaleFactor.TwoX,
                TotalPages = 1,
            };

            var reported = new List<UpscaleJobFile>();

            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
                client.RunChapterAsync(
                    request,
                    OnePage(),
                    progress: null,
                    onPageDone: reported.Add,
                    cancellationToken: CancellationToken.None,
                    timeout: null
                )
            );
            Assert.IsType(expectedType, thrown);

            // No per-page error was recorded: the exception left the pipeline as itself, which
            // is what the classifier needs to see.
            Assert.Empty(reported);
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_AggregateWithAPermanentFirstAndIoSecond_PropagatesAsInfrastructure()
    {
        // The classifier's own doc names "an IOException inside an AggregateException" as the
        // retryable case it must see. When the walk followed only InnerException, the first sibling
        // (the permanent one) hid the IO failure, so the page was recorded as a per-page error instead
        // of the task being requeued with its spool.
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
                Task.FromException<Task>(
                    new AggregateException(
                        new InvalidOperationException("permanent"),
                        new IOException("disk gone")
                    )
                )
            );

        var client = new MangaJaNaiWorkerClient(
            engine,
            Substitute.For<IOnnxSessionFactory>(),
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        string outputDir = Directory.CreateTempSubdirectory("worker_client_aggregate").FullName;
        try
        {
            var request = new ChapterJobRequest
            {
                Id = "job",
                OutputFolder = outputDir,
                Format = CompressionFormat.Webp,
                Scale = ScaleFactor.TwoX,
                TotalPages = 1,
            };

            var reported = new List<UpscaleJobFile>();

            await Assert.ThrowsAsync<AggregateException>(() =>
                client.RunChapterAsync(
                    request,
                    OnePage(),
                    progress: null,
                    onPageDone: reported.Add,
                    cancellationToken: CancellationToken.None,
                    timeout: null
                )
            );

            Assert.Empty(reported);
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_WhenTheEngineStalls_SurfacesTheInactivityTimeout()
    {
        // The engine's staged write task never completes on its own but observes the linked
        // cancellation token, the way the real engine's encoder does. The client's inactivity clock
        // (timeoutCts.CancelAfter) must fire and its OperationCanceledException catch must turn the
        // cancellation into a TimeoutException — the transient signal the streaming classifier
        // requeues. Without either the chapter would hang (the WaitAsync bound catches that) or the
        // raw OCE would escape as a permanent failure.
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(ci => Task.FromResult(NeverCompletingWrite(ci.Arg<CancellationToken>())));

        var client = new MangaJaNaiWorkerClient(
            engine,
            Substitute.For<IOnnxSessionFactory>(),
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        string outputDir = Directory.CreateTempSubdirectory("worker_client_timeout").FullName;
        try
        {
            var request = new ChapterJobRequest
            {
                Id = "stalled",
                OutputFolder = outputDir,
                Format = CompressionFormat.Webp,
                Scale = ScaleFactor.TwoX,
                TotalPages = 1,
            };

            var reported = new List<UpscaleJobFile>();

            TimeoutException error = await Assert
                .ThrowsAsync<TimeoutException>(() =>
                    client.RunChapterAsync(
                        request,
                        OnePage(),
                        progress: null,
                        onPageDone: reported.Add,
                        cancellationToken: CancellationToken.None,
                        timeout: TimeSpan.FromMilliseconds(200)
                    )
                )
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Contains("inactivity", error.Message, StringComparison.Ordinal);
            Assert.Empty(reported);
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    private static async Task NeverCompletingWrite(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async IAsyncEnumerable<ChapterPage> OnePage()
    {
        yield return new ChapterPage(0, "page0.png", "page0.png");
    }
}
