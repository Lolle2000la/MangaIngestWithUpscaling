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

    private static async IAsyncEnumerable<ChapterPage> OnePage()
    {
        yield return new ChapterPage(0, "page0.png", "page0.png");
    }
}
