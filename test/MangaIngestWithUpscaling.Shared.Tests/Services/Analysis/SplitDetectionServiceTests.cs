using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

[Collection("SplitDetectionLayout")]
public class SplitDetectionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("split_detect_cli").FullName;
    private readonly string _previousRoot = SplitDetectionLayout.Root;

    public SplitDetectionServiceTests()
    {
        SplitDetectionLayout.Root = _root;
    }

    public void Dispose()
    {
        SplitDetectionLayout.Root = _previousRoot;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenCancellationTokenCancelled_ThrowsOperationCanceledException()
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer
        );

        string image = Path.Combine(_root, "page.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DetectSplitsAsync(image, cancellationToken: cts.Token)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer
        );

        string image = Path.Combine(_root, "non_existent.png");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectSplitsAsync_WhenModelDoesNotExist_ThrowsFileNotFoundException()
    {
        var factory = Substitute.For<IOnnxSessionFactory>();
        var localizer = Substitute.For<IStringLocalizer<SplitDetectionService>>();
        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = Path.Combine(_root, "empty_models") }
        );

        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            localizer,
            config
        );

        string image = Path.Combine(_root, "sample.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DetectSplitsAsync(
                image,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }
}

/// <summary>
/// Serializes tests that mutate the process-wide <see cref="SplitDetectionLayout.Root" />.
/// </summary>
[CollectionDefinition("SplitDetectionLayout", DisableParallelization = true)]
public class SplitDetectionLayoutCollection;
