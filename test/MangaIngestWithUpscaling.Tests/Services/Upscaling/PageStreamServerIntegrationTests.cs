extern alias remote;

using System.IO.Compression;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Grpc.Net.Client;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using RemotePageStreamClient = remote::MangaIngestWithUpscaling.RemoteWorker.Background.PageStreamClient;
using RemoteUpscalingServiceClient = remote::MangaIngestWithUpscaling.Api.Upscaling.UpscalingService.UpscalingServiceClient;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using SharedUpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

/// <summary>
/// Exercises the page-streaming transport over a real gRPC channel against the real
/// <see cref="UpscalingDistributionService"/>, a real <see cref="PageStreamSpool"/> and a real
/// database, with only the local Python worker faked. This covers what the stubbed-client test
/// cannot: gRPC serialization, the real handlers, identity/spool persistence and the chapter
/// completion path.
///
/// The remote worker project is referenced with an alias because it and the main app both generate
/// the same protobuf types.
/// </summary>
public sealed class PageStreamServerIntegrationTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private WebApplication _app = null!;
    private GrpcChannel _channel = null!;
    private string _root = null!;
    private string _upscaledPath = null!;
    private int _taskId;
    private int _detectTaskId;
    private int _chapterId;
    private SharedUpscalerProfile _profile = null!;
    private ISplitProcessingService _splitProcessing = null!;

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("page_stream_server").FullName;
        string notUpscaledDir = Path.Combine(_root, "not_upscaled");
        string upscaledDir = Path.Combine(_root, "upscaled");
        Directory.CreateDirectory(notUpscaledDir);
        Directory.CreateDirectory(upscaledDir);

        string sourcePath = Path.Combine(notUpscaledDir, "Series", "Chapter 1.cbz");
        _upscaledPath = Path.Combine(upscaledDir, "Series", "Chapter 1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        CreateSourceCbz(sourcePath);

        _database = TestDatabaseFactory.Create();
        _profile = new SharedUpscalerProfile
        {
            Name = "test",
            CompressionFormat = SharedCompressionFormat.Webp,
            ScalingFactor = SharedScaleFactor.TwoX,
            Quality = 80,
        };

        await SeedAsync(notUpscaledDir, upscaledDir);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddOptions();
        builder.Services.AddDbContext<ApplicationDbContext>(o => _database.Configure(o));
        builder.Services.AddSingleton<TaskQueue>();
        builder.Services.AddSingleton<ITaskPersistenceService, TaskPersistenceService>();
        builder.Services.AddSingleton<DistributedUpscaleTaskProcessor>();
        builder.Services.AddSingleton<PageStreamSpool>();
        builder.Services.AddSingleton<IUpscalerJsonHandlingService>(
            new UpscalerJsonHandlingService(Substitute.For<ILogger<UpscalerJsonHandlingService>>())
        );
        builder.Services.AddSingleton<IFileSystem>(new GenericFileSystem());
        builder.Services.AddSingleton<IMetadataHandlingService>(
            new MetadataHandlingService(Substitute.For<ILogger<MetadataHandlingService>>())
        );
        builder.Services.AddSingleton(Substitute.For<IChapterChangedNotifier>());
        _splitProcessing = Substitute.For<ISplitProcessingService>();
        builder.Services.AddSingleton(_splitProcessing);
        builder.Services.AddSingleton(Substitute.For<ISplitProcessingCoordinator>());

        // The distribution service is [Authorize(AuthenticationSchemes = "ApiKey")]; the test
        // authenticates every request so the transport, not auth, is under test.
        builder
            .Services.AddAuthentication("ApiKey")
            .AddScheme<AuthenticationSchemeOptions, TestApiKeyHandler>("ApiKey", null);
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGrpcService<UpscalingDistributionService>();
        await _app.StartAsync();

        TestServer server = _app.GetTestServer();
        _channel = GrpcChannel.ForAddress(
            server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = server.CreateHandler() }
        );
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _app.DisposeAsync();
        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StreamsAChapterOverRealGrpcAndMarksItUpscaled()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        var worker = new FakeWorkerClient();
        var sut = new RemotePageStreamClient(
            worker,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );

        await sut.RunAsync(client, _taskId, _profile, TestContext.Current.CancellationToken);

        Assert.Equal(2, worker.ProcessedPages);
        Assert.True(File.Exists(_upscaledPath));

        using ZipArchive zip = ZipFile.OpenRead(_upscaledPath);
        List<string> entries = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(4, entries.Count);
        Assert.Contains("001.webp", entries);
        Assert.Contains("002.webp", entries);
        Assert.Contains("ComicInfo.xml", entries);
        Assert.Contains("upscaler.json", entries);
        Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
        Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
        Assert.Equal("<ComicInfo/>"u8.ToArray(), ReadEntry(zip, "ComicInfo.xml"));

        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );
        Chapter chapter = await context.Chapters.FirstAsync(
            c => c.Id == _chapterId,
            TestContext.Current.CancellationToken
        );
        Assert.True(chapter.IsUpscaled);
        PersistedTask task = await context.PersistedTasks.FirstAsync(
            t => t.Id == _taskId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(PersistedTaskStatus.Completed, task.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResumesAnInterruptedChapterOverRealGrpc()
    {
        var client = new RemoteUpscalingServiceClient(_channel);

        // The first worker drops after the first page, which the real server already spooled.
        var droppingWorker = new FakeWorkerClient { DropAfterPages = 1 };
        var dropping = new RemotePageStreamClient(
            droppingWorker,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );
        await Assert.ThrowsAnyAsync<Exception>(() =>
            dropping.RunAsync(client, _taskId, _profile, TestContext.Current.CancellationToken)
        );
        Assert.False(File.Exists(_upscaledPath));

        // The retry resumes: the server reports the first page as already spooled and the worker
        // only has to produce the second.
        var recoveringWorker = new FakeWorkerClient();
        var recovering = new RemotePageStreamClient(
            recoveringWorker,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );
        await recovering.RunAsync(client, _taskId, _profile, TestContext.Current.CancellationToken);

        Assert.Equal(1, recoveringWorker.ProcessedPages);
        Assert.True(File.Exists(_upscaledPath));

        using ZipArchive zip = ZipFile.OpenRead(_upscaledPath);
        Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
        Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StreamsDetectionOverRealGrpc()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        var detection = Substitute.For<ISplitDetectionService>();
        detection
            .DetectSplitsAsync(
                Arg.Any<string>(),
                Arg.Any<IProgress<UpscaleProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(ci => new List<SplitDetectionResult>
            {
                new()
                {
                    ImagePath = ci.Arg<string>(),
                    Splits =
                    {
                        new DetectedSplit { YOriginal = 42, Confidence = 0.9 },
                    },
                    Count = 1,
                },
            });

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(detection)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var sut = new RemotePageStreamClient(
            Substitute.For<IMangaJaNaiWorkerClient>(),
            scopeFactory,
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );

        await sut.RunDetectionAsync(client, _detectTaskId, TestContext.Current.CancellationToken);

        await _splitProcessing
            .Received(1)
            .ProcessDetectionResultsAsync(
                _chapterId,
                Arg.Is<List<SplitDetectionResult>>(r => r.Count == 2),
                1,
                Arg.Any<CancellationToken>()
            );
    }

    private async Task SeedAsync(string notUpscaledDir, string upscaledDir)
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync();

        var library = new Library
        {
            Name = "Library",
            NotUpscaledLibraryPath = notUpscaledDir,
            UpscaledLibraryPath = upscaledDir,
        };
        var manga = new Manga { PrimaryTitle = "Series", Library = library };
        var chapter = new Chapter
        {
            Manga = manga,
            FileName = "Chapter 1.cbz",
            RelativePath = Path.Combine("Series", "Chapter 1.cbz"),
        };
        context.AddRange(library, _profile, manga, chapter);
        await context.SaveChangesAsync();

        var task = new PersistedTask
        {
            Data = new UpscaleTask { ChapterId = chapter.Id, UpscalerProfileId = _profile.Id },
            Status = PersistedTaskStatus.Pending,
        };
        context.PersistedTasks.Add(task);
        await context.SaveChangesAsync();

        var detectTask = new PersistedTask
        {
            Data = new DetectSplitCandidatesTask { ChapterId = chapter.Id, DetectorVersion = 1 },
            Status = PersistedTaskStatus.Pending,
        };
        context.PersistedTasks.Add(detectTask);
        await context.SaveChangesAsync();

        _chapterId = chapter.Id;
        _taskId = task.Id;
        _detectTaskId = detectTask.Id;
    }

    private static void CreateSourceCbz(string path)
    {
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
        WriteEntry(zip, "002.jpg", new byte[] { 4, 5, 6 });
        WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
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

    /// <summary>
    /// Fake local worker: "upscales" each page by reversing its bytes. Optionally throws after N
    /// pages to simulate a worker/connection drop.
    /// </summary>
    private sealed class FakeWorkerClient : IMangaJaNaiWorkerClient
    {
        public int? DropAfterPages { get; init; }
        public int ProcessedPages { get; private set; }

        public async Task<UpscaleJobResult> RunChapterAsync(
            ChapterJobRequest request,
            IAsyncEnumerable<ChapterPage> pages,
            IProgress<UpscaleProgress>? progress,
            Action<UpscaleJobFile> onPageDone,
            CancellationToken cancellationToken,
            TimeSpan? timeout
        )
        {
            var files = new List<UpscaleJobFile>();
            await foreach (ChapterPage page in pages.WithCancellation(cancellationToken))
            {
                byte[] source = await File.ReadAllBytesAsync(page.Path, cancellationToken);
                string outputPath = Path.Combine(
                    request.OutputFolder,
                    $"{Path.GetFileNameWithoutExtension(page.Name)}.webp"
                );
                await File.WriteAllBytesAsync(
                    outputPath,
                    source.Reverse().ToArray(),
                    cancellationToken
                );

                var file = new UpscaleJobFile(page.Name, outputPath, "upscaled");
                files.Add(file);
                onPageDone(file);
                ProcessedPages++;

                if (DropAfterPages is int dropAt && ProcessedPages >= dropAt)
                {
                    throw new IOException("simulated drop");
                }
            }

            return new UpscaleJobResult(request.Id, "ok", files, 0);
        }

        public Task<UpscaleJobResult> RunJobAsync(
            UpscaleJobRequest request,
            IProgress<UpscaleProgress>? progress,
            CancellationToken cancellationToken,
            TimeSpan? timeout
        ) => throw new NotSupportedException();

        public Task ShutdownWorkerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    /// <summary>Authenticates every request as the API-key scheme.</summary>
    private sealed class TestApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestApiKeyHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder
        )
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity("Test"));
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
            );
        }
    }
}
