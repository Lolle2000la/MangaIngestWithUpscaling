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
            .UpscaleFileAsync(
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
            .UpscaleFileAsync(
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
