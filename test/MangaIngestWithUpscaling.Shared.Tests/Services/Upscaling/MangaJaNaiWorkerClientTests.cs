using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class MangaJaNaiWorkerClientTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(CompressionFormat.Webp, "webp")]
    [InlineData(CompressionFormat.Png, "png")]
    [InlineData(CompressionFormat.Jpg, "jpeg")]
    [InlineData(CompressionFormat.Avif, "avif")]
    public void ToFormatString_MapsCompressionFormatToWorkerFormat(
        CompressionFormat format,
        string expected
    )
    {
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToFormatString(format));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(CompressionFormat.Webp, ".webp")]
    [InlineData(CompressionFormat.Png, ".png")]
    [InlineData(CompressionFormat.Jpg, ".jpg")]
    [InlineData(CompressionFormat.Avif, ".avif")]
    public void ToExtension_MapsCompressionFormatToExtension(
        CompressionFormat format,
        string expected
    )
    {
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToExtension(format));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunJobAsync_WithCbzRequest_DelegatesToUpscaleEngine()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();
        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new UpscaleJobRequest
        {
            Id = "job-1",
            InputPath = "/data/ch1.cbz",
            OutputFolder = "/out",
            OutputFilename = "chapter-1",
            Format = CompressionFormat.Webp,
            Scale = ScaleFactor.TwoX,
            Overwrite = true,
        };

        var result = await client.RunJobAsync(
            request,
            null,
            TestContext.Current.CancellationToken,
            null
        );

        Assert.Equal("job-1", result.Id);
        Assert.Equal("success", result.Status);
        Assert.Single(result.Files);

        await engine
            .Received(1)
            .UpscaleCbzAsync(
                "/data/ch1.cbz",
                Path.Combine("/out", "chapter-1.cbz"),
                2,
                CompressionFormat.Webp,
                null,
                null,
                TestContext.Current.CancellationToken
            );

        factory.DidNotReceive().InvalidateAllSessions();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunJobAsync_WhenTimeoutExceeded_ThrowsTimeoutException()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();
        engine
            .UpscaleCbzAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async x =>
            {
                var ct = x.Arg<CancellationToken>();
                await Task.Delay(500, ct);
            });

        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new UpscaleJobRequest
        {
            Id = "job-timeout",
            InputPath = "/data/ch1.cbz",
            OutputFolder = "/out",
            OutputFilename = "chapter-1",
            Format = CompressionFormat.Webp,
            Scale = ScaleFactor.TwoX,
            Overwrite = true,
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.RunJobAsync(
                request,
                null,
                TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(50)
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();
        using var cts = new CancellationTokenSource();

        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async x =>
            {
                var ct = x.Arg<CancellationToken>();
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new ChapterJobRequest
        {
            Id = "ch-cancel",
            OutputFolder = "/out",
            Scale = ScaleFactor.TwoX,
            Format = CompressionFormat.Webp,
            Quality = 90,
            TotalPages = 1,
        };

        async IAsyncEnumerable<ChapterPage> GetPages()
        {
            yield return new ChapterPage(0, "001.png", "/data/001.png");
            await Task.Yield();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RunChapterAsync(request, GetPages(), null, _ => { }, cts.Token, null)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_WhenTimeoutExceeded_ThrowsTimeoutException()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();

        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async x =>
            {
                var ct = x.Arg<CancellationToken>();
                await Task.Delay(500, ct);
                return Task.CompletedTask;
            });

        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new ChapterJobRequest
        {
            Id = "ch-timeout",
            OutputFolder = "/out",
            Scale = ScaleFactor.TwoX,
            Format = CompressionFormat.Webp,
            Quality = 90,
            TotalPages = 1,
        };

        async IAsyncEnumerable<ChapterPage> GetPages()
        {
            yield return new ChapterPage(0, "001.png", "/data/001.png");
            await Task.Yield();
        }

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.RunChapterAsync(
                request,
                GetPages(),
                null,
                _ => { },
                TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(50)
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_StartsNextInferenceBeforePreviousEncodeCompletes()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();
        var firstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondInferenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(x =>
            {
                string input = x.ArgAt<string>(0);
                if (input.EndsWith("001.png"))
                {
                    return Task.FromResult<Task>(firstWrite.Task);
                }

                // The second page's inference runs while the first page is still encoding.
                secondInferenceStarted.SetResult();
                firstWrite.SetResult();
                return Task.FromResult(Task.CompletedTask);
            });

        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new ChapterJobRequest
        {
            Id = "ch-pipeline",
            OutputFolder = "/out",
            Scale = ScaleFactor.TwoX,
            Format = CompressionFormat.Webp,
            Quality = 90,
            TotalPages = 2,
        };

        async IAsyncEnumerable<ChapterPage> GetPages()
        {
            yield return new ChapterPage(0, "001.png", "/data/001.png");
            yield return new ChapterPage(1, "002.png", "/data/002.png");
            await Task.Yield();
        }

        var done = new List<UpscaleJobFile>();
        UpscaleJobResult result = await client
            .RunChapterAsync(
                request,
                GetPages(),
                null,
                done.Add,
                TestContext.Current.CancellationToken,
                null
            )
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(secondInferenceStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(["/data/001.png", "/data/002.png"], done.Select(f => f.Input));
        Assert.All(done, f => Assert.Equal("success", f.Status));
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunChapterAsync_WhenEncodeFails_ReportsPageErrorAndContinues()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();

        engine
            .UpscaleFileStagedAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CompressionFormat>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(x =>
                x.ArgAt<string>(0).EndsWith("001.png")
                    ? Task.FromResult(Task.FromException(new IOException("disk full")))
                    : Task.FromResult(Task.CompletedTask)
            );

        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        var request = new ChapterJobRequest
        {
            Id = "ch-encode-fail",
            OutputFolder = "/out",
            Scale = ScaleFactor.TwoX,
            Format = CompressionFormat.Webp,
            Quality = 90,
            TotalPages = 2,
        };

        async IAsyncEnumerable<ChapterPage> GetPages()
        {
            yield return new ChapterPage(0, "001.png", "/data/001.png");
            yield return new ChapterPage(1, "002.png", "/data/002.png");
            await Task.Yield();
        }

        var done = new List<UpscaleJobFile>();
        await client.RunChapterAsync(
            request,
            GetPages(),
            null,
            done.Add,
            TestContext.Current.CancellationToken,
            null
        );

        Assert.Equal(["error", "success"], done.Select(f => f.Status));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReleaseGpuCacheAsync_InvalidatesAllSessions()
    {
        var engine = Substitute.For<IOnnxUpscaleEngine>();
        var factory = Substitute.For<IOnnxSessionFactory>();
        var client = new MangaJaNaiWorkerClient(
            engine,
            factory,
            NullLogger<MangaJaNaiWorkerClient>.Instance
        );

        bool success = await client.ReleaseGpuCacheAsync(TestContext.Current.CancellationToken);

        Assert.True(success);
        factory.Received(1).InvalidateAllSessions();
    }
}
