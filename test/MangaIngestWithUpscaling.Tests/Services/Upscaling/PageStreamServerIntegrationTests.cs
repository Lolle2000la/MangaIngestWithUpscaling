extern alias remote;

using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Net.Client;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
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
using RemoteEngineIdentityProvider = remote::MangaIngestWithUpscaling.RemoteWorker.Background.IEngineIdentityProvider;
using RemotePageManifestRequest = remote::MangaIngestWithUpscaling.Api.Upscaling.PageManifestRequest;
using RemotePageManifestResponse = remote::MangaIngestWithUpscaling.Api.Upscaling.PageManifestResponse;
using RemotePageStreamClient = remote::MangaIngestWithUpscaling.RemoteWorker.Background.PageStreamClient;
using RemotePageStreamRestartException = remote::MangaIngestWithUpscaling.RemoteWorker.Background.PageStreamRestartException;
using RemoteUploadDetectionResultResponse = remote::MangaIngestWithUpscaling.Api.Upscaling.UploadDetectionResultResponse;
using RemoteUploadPageChunk = remote::MangaIngestWithUpscaling.Api.Upscaling.UploadPageChunk;
using RemoteUploadPageDetectionRequest = remote::MangaIngestWithUpscaling.Api.Upscaling.UploadPageDetectionRequest;
using RemoteUploadPageResponse = remote::MangaIngestWithUpscaling.Api.Upscaling.UploadPageResponse;
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
    private int _repairTaskId;
    private int _chapterId;
    private SharedUpscalerProfile _profile = null!;
    private ISplitProcessingService _splitProcessing = null!;
    private IMetadataHandlingService _metadata = null!;

    // Fixed engine identities so the raw-RPC manifest requests, the manual spool seeding and the
    // real PageStreamClient all agree on the engine.
    private const string UpscalerEngineIdentity = "test-upscaler-engine";
    private const string DetectorEngineIdentity = "test-detector-engine";

    private static RemoteEngineIdentityProvider StubEngineIdentity() =>
        new StubEngineIdentityProvider(UpscalerEngineIdentity, DetectorEngineIdentity);

    private static RemotePageStreamClient CreatePageStreamClient(FakeWorkerClient worker) =>
        new(
            worker,
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig { ImageFormatConversionRules = [] }),
            StubEngineIdentity(),
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );

    private sealed class StubEngineIdentityProvider(string upscaler, string detector)
        : RemoteEngineIdentityProvider
    {
        public string Upscaler { get; } = upscaler;
        public string Detector { get; } = detector;
    }

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
        builder.Services.AddSingleton<PageContextCache>();
        builder.Services.AddSingleton<IUpscalerJsonHandlingService>(
            new UpscalerJsonHandlingService(Substitute.For<ILogger<UpscalerJsonHandlingService>>())
        );
        builder.Services.AddSingleton<IFileSystem>(new GenericFileSystem());
        _metadata = Substitute.For<IMetadataHandlingService>();
        builder.Services.AddSingleton(_metadata);
        builder.Services.AddSingleton(Substitute.For<IImageResizeService>());
        builder.Services.AddScoped<IRepairService, RepairService>();
        builder.Services.AddSingleton(Substitute.For<IMangaMetadataChanger>());
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
        var sut = CreatePageStreamClient(worker);

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
    public async Task UploadPage_RejectsATruncatedStreamWithoutTheFinalChunk()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _taskId,
                EngineIdentity = UpscalerEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var call = client.UploadPage(
            cancellationToken: TestContext.Current.CancellationToken
        );
        await call.RequestStream.WriteAsync(
            new RemoteUploadPageChunk
            {
                TaskId = _taskId,
                PageIndex = 0,
                ChunkNumber = 0,
                Chunk = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
                ContentIdentity = manifest.TaskIdentity,
            },
            TestContext.Current.CancellationToken
        );
        // No IsLast terminator: a gracefully-closed but truncated stream must be rejected rather
        // than committed as a whole page.
        await call.RequestStream.CompleteAsync();
        RemoteUploadPageResponse response = await call.ResponseAsync;

        Assert.False(response.Success);
        Assert.Contains("final chunk", response.Message);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UploadPage_RejectsAStaleIdentityOverTheRpcBoundary()
    {
        var client = new RemoteUpscalingServiceClient(_channel);

        using var call = client.UploadPage(
            cancellationToken: TestContext.Current.CancellationToken
        );
        await call.RequestStream.WriteAsync(
            new RemoteUploadPageChunk
            {
                TaskId = _taskId,
                PageIndex = 0,
                ChunkNumber = 0,
                Chunk = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
                ContentIdentity = "not-the-current-identity",
            },
            TestContext.Current.CancellationToken
        );
        await call.RequestStream.WriteAsync(
            new RemoteUploadPageChunk
            {
                TaskId = _taskId,
                PageIndex = 0,
                ChunkNumber = 1,
                Chunk = ByteString.Empty,
                IsLast = true,
                ContentIdentity = "not-the-current-identity",
            },
            TestContext.Current.CancellationToken
        );
        await call.RequestStream.CompleteAsync();
        RemoteUploadPageResponse response = await call.ResponseAsync;

        Assert.False(response.Success);
        Assert.Contains("changed", response.Message);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UploadPage_RejectsAnUploadFromADifferentEngine()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _taskId,
                EngineIdentity = UpscalerEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        // The chapter is spooled with UpscalerEngineIdentity; a page from another engine must not be
        // committed (it would mix engines in one CBZ).
        using var call = client.UploadPage(
            cancellationToken: TestContext.Current.CancellationToken
        );
        await call.RequestStream.WriteAsync(
            new RemoteUploadPageChunk
            {
                TaskId = _taskId,
                PageIndex = 0,
                ChunkNumber = 0,
                Chunk = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
                ContentIdentity = manifest.TaskIdentity,
                EngineIdentity = "another-engine",
            },
            TestContext.Current.CancellationToken
        );
        await call.RequestStream.WriteAsync(
            new RemoteUploadPageChunk
            {
                TaskId = _taskId,
                PageIndex = 0,
                ChunkNumber = 1,
                Chunk = ByteString.Empty,
                IsLast = true,
                ContentIdentity = manifest.TaskIdentity,
                EngineIdentity = "another-engine",
            },
            TestContext.Current.CancellationToken
        );
        await call.RequestStream.CompleteAsync();
        RemoteUploadPageResponse response = await call.ResponseAsync;

        Assert.False(response.Success);
        Assert.Contains("engine", response.Message);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPageManifest_ResetsTheSpoolWhenTheEngineChanges()
    {
        var client = new RemoteUpscalingServiceClient(_channel);

        // Spool one page under the test engine (the worker drops after it).
        var dropping = CreatePageStreamClient(new FakeWorkerClient { DropAfterPages = 1 });
        await Assert.ThrowsAnyAsync<Exception>(() =>
            dropping.RunAsync(client, _taskId, _profile, TestContext.Current.CancellationToken)
        );

        RemotePageManifestResponse withSameEngine = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _taskId,
                EngineIdentity = UpscalerEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Single(withSameEngine.CompletedPages);

        // A manifest from a different engine must discard the spool so the two engines never mix.
        RemotePageManifestResponse withOtherEngine = await client.GetPageManifestAsync(
            new RemotePageManifestRequest { TaskId = _taskId, EngineIdentity = "another-engine" },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Empty(withOtherEngine.CompletedPages);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UploadPageDetection_RejectsAStaleIdentityNonTerminally()
    {
        var client = new RemoteUpscalingServiceClient(_channel);

        // Create the session via the manifest so the rejection is about the identity, not the spool.
        await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _detectTaskId,
                EngineIdentity = DetectorEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        RemoteUploadDetectionResultResponse response = await client.UploadPageDetectionAsync(
            new RemoteUploadPageDetectionRequest
            {
                TaskId = _detectTaskId,
                PageIndex = 0,
                ResultJson = "{}",
                TaskIdentity = "not-the-current-identity",
                EngineIdentity = DetectorEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        // A stale identity means "restart", not "fail", so the worker must not report a failure
        // that would discard every already-detected page.
        Assert.False(response.Success);
        Assert.False(response.Terminal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UploadPageDetection_RejectsMalformedJsonTerminally()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _detectTaskId,
                EngineIdentity = DetectorEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        RemoteUploadDetectionResultResponse response = await client.UploadPageDetectionAsync(
            new RemoteUploadPageDetectionRequest
            {
                TaskId = _detectTaskId,
                PageIndex = 0,
                ResultJson = "not json",
                TaskIdentity = manifest.TaskIdentity,
                EngineIdentity = DetectorEngineIdentity,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(response.Success);
        Assert.True(response.Terminal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResumesAnInterruptedChapterOverRealGrpc()
    {
        var client = new RemoteUpscalingServiceClient(_channel);

        // The first worker drops after the first page, which the real server already spooled.
        var droppingWorker = new FakeWorkerClient { DropAfterPages = 1 };
        var dropping = CreatePageStreamClient(droppingWorker);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            dropping.RunAsync(client, _taskId, _profile, TestContext.Current.CancellationToken)
        );
        Assert.False(File.Exists(_upscaledPath));

        // The retry resumes: the server reports the first page as already spooled and the worker
        // only has to produce the second.
        var recoveringWorker = new FakeWorkerClient();
        var recovering = CreatePageStreamClient(recoveringWorker);
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
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>()
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
            Options.Create(new UpscalerConfig()),
            StubEngineIdentity(),
            Substitute.For<ILogger<RemotePageStreamClient>>()
        );

        await sut.RunDetectionAsync(client, _detectTaskId, TestContext.Current.CancellationToken);

        await _splitProcessing
            .Received(1)
            .ProcessDetectionResultsAsync(
                _chapterId,
                Arg.Is<List<SplitDetectionResult>>(r =>
                    r.Count == 2
                    && r.Select(x => Path.GetFileNameWithoutExtension(x.ImagePath))
                        .OrderBy(x => x)
                        .SequenceEqual(new[] { "001", "002" })
                ),
                1,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPageManifest_FinalizesAnAlreadyCompleteUpscaleStream()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _taskId,
                EngineIdentity = UpscalerEngineIdentity,
            },
            deadline: DateTime.UtcNow.AddSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Simulate a previous run that spooled every page but failed to finalize.
        var spool = _app.Services.GetRequiredService<PageStreamSpool>();
        PageStreamSession session = spool.GetOrCreateSession(
            _taskId,
            manifest.TaskIdentity,
            UpscalerEngineIdentity,
            manifest.Pages.Count
        );
        foreach (var page in manifest.Pages)
        {
            await spool.WritePageAsync(
                session,
                page.Index,
                new MemoryStream(new byte[] { (byte)(page.Index + 10) }),
                TestContext.Current.CancellationToken
            );
        }

        RemotePageManifestResponse second = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _taskId,
                EngineIdentity = UpscalerEngineIdentity,
            },
            deadline: DateTime.UtcNow.AddSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Complete);
        Assert.True(File.Exists(_upscaledPath));
        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );
        Chapter chapter = await context.Chapters.FirstAsync(
            c => c.Id == _chapterId,
            TestContext.Current.CancellationToken
        );
        Assert.True(chapter.IsUpscaled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPageManifest_FinalizesAnAlreadyCompleteDetectionStream()
    {
        var client = new RemoteUpscalingServiceClient(_channel);
        RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _detectTaskId,
                EngineIdentity = DetectorEngineIdentity,
            },
            deadline: DateTime.UtcNow.AddSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var spool = _app.Services.GetRequiredService<PageStreamSpool>();
        PageStreamSession session = spool.GetOrCreateSession(
            _detectTaskId,
            manifest.TaskIdentity,
            DetectorEngineIdentity,
            manifest.Pages.Count
        );
        foreach (var page in manifest.Pages)
        {
            string json = JsonSerializer.Serialize(
                new SplitDetectionResult { ImagePath = page.SourceName, Count = 0 },
                SharedJsonContext.Default.SplitDetectionResult
            );
            await spool.WritePageAsync(
                session,
                page.Index,
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                TestContext.Current.CancellationToken
            );
        }

        RemotePageManifestResponse second = await client.GetPageManifestAsync(
            new RemotePageManifestRequest
            {
                TaskId = _detectTaskId,
                EngineIdentity = DetectorEngineIdentity,
            },
            deadline: DateTime.UtcNow.AddSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Complete);
        await _splitProcessing
            .Received(1)
            .ProcessDetectionResultsAsync(
                _chapterId,
                Arg.Is<List<SplitDetectionResult>>(r =>
                    r.Count == 2
                    && r.Select(x => Path.GetFileNameWithoutExtension(x.ImagePath))
                        .OrderBy(x => x)
                        .SequenceEqual(new[] { "001", "002" })
                ),
                1,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPageManifest_ResolvesAndCachesTheRepairContext()
    {
        // Repair resolves its context by diffing the source against the upscaled chapter; that must
        // happen once per identity, not on every manifest/page RPC.
        Directory.CreateDirectory(Path.GetDirectoryName(_upscaledPath)!);
        CreateSourceCbz(_upscaledPath);
        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new PageDifferenceResult(new[] { "001" }, Array.Empty<string>()));

        var client = new RemoteUpscalingServiceClient(_channel);
        for (int i = 0; i < 3; i++)
        {
            RemotePageManifestResponse manifest = await client.GetPageManifestAsync(
                new RemotePageManifestRequest
                {
                    TaskId = _repairTaskId,
                    EngineIdentity = UpscalerEngineIdentity,
                },
                deadline: DateTime.UtcNow.AddSeconds(30),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.False(manifest.Complete);
            Assert.Single(manifest.Pages);
            Assert.Equal("001.jpg", manifest.Pages[0].SourceName);
            Assert.Equal("001.webp", manifest.Pages[0].OutputName);
        }

        await _metadata
            .Received(1)
            .AnalyzePageDifferencesAsync(Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepairsAMissingPageOverRealGrpcAndMergesIt()
    {
        // The upscaled chapter is missing "001"; the source still has it.
        Directory.CreateDirectory(Path.GetDirectoryName(_upscaledPath)!);
        using (ZipArchive zip = ZipFile.Open(_upscaledPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "002.webp", new byte[] { 6, 5, 4 });
        }

        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new PageDifferenceResult(new[] { "001" }, Array.Empty<string>()));

        // Prepare the real remote repair state (extracts the archives, builds the missing-pages
        // CBZ); the internal preparation method is invoked directly to avoid driving the whole
        // dispatch loop.
        var processor = _app.Services.GetRequiredService<DistributedUpscaleTaskProcessor>();
        using (IServiceScope scope = _app.Services.CreateScope())
        {
            PersistedTask repairTask = await LoadTaskAsync(_repairTaskId);
            Assert.True(
                await processor.PrepareRepairTaskForRemote(
                    (RepairUpscaleTask)repairTask.Data,
                    repairTask,
                    scope.ServiceProvider,
                    CancellationToken.None
                )
            );
        }

        var client = new RemoteUpscalingServiceClient(_channel);
        var sut = CreatePageStreamClient(new FakeWorkerClient());

        await sut.RunAsync(client, _repairTaskId, _profile, TestContext.Current.CancellationToken);

        using ZipArchive result = ZipFile.OpenRead(_upscaledPath);
        Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(result, "001.webp"));
        Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(result, "002.webp"));

        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );
        PersistedTask task = await context.PersistedTasks.FirstAsync(
            t => t.Id == _repairTaskId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(PersistedTaskStatus.Completed, task.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepairFinalizeWithoutPreparedStateRestartsNonTerminally()
    {
        // The upscaled chapter is missing "001"; the source still has it.
        Directory.CreateDirectory(Path.GetDirectoryName(_upscaledPath)!);
        using (ZipArchive zip = ZipFile.Open(_upscaledPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "002.webp", new byte[] { 6, 5, 4 });
        }

        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new PageDifferenceResult(new[] { "001" }, Array.Empty<string>()));

        // Deliberately do NOT prepare the remote repair state: a finalize that lands on a replica
        // without it (or after a requeue cleaned it) must restart the chapter, not fail it terminally.
        var client = new RemoteUpscalingServiceClient(_channel);
        var sut = CreatePageStreamClient(new FakeWorkerClient());

        await Assert.ThrowsAsync<RemotePageStreamRestartException>(() =>
            sut.RunAsync(client, _repairTaskId, _profile, TestContext.Current.CancellationToken)
        );

        await using ApplicationDbContext context = await _database.CreateContextAsync(
            TestContext.Current.CancellationToken
        );
        PersistedTask task = await context.PersistedTasks.FirstAsync(
            t => t.Id == _repairTaskId,
            TestContext.Current.CancellationToken
        );
        Assert.NotEqual(PersistedTaskStatus.Completed, task.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepairsANestedMissingPageOverRealGrpcAndMergesIt()
    {
        // Nested archive: the upscaled chapter has ch1/001.webp but is missing ch1/002.webp.
        string nestedNotUpscaledDir = Path.Combine(_root, "nested_not_upscaled");
        string nestedUpscaledDir = Path.Combine(_root, "nested_upscaled");
        Directory.CreateDirectory(nestedNotUpscaledDir);
        Directory.CreateDirectory(nestedUpscaledDir);

        string nestedSource = Path.Combine(nestedNotUpscaledDir, "Series", "Chapter 2.cbz");
        string nestedUpscaled = Path.Combine(nestedUpscaledDir, "Series", "Chapter 2.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nestedUpscaled)!);
        using (ZipArchive zip = ZipFile.Open(nestedSource, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "ch1/001.jpg", new byte[] { 1, 2, 3 });
            WriteEntry(zip, "ch1/002.jpg", new byte[] { 4, 5, 6 });
        }
        using (ZipArchive zip = ZipFile.Open(nestedUpscaled, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "ch1/001.webp", new byte[] { 9, 9, 9 });
        }

        int nestedTaskId = await SeedNestedRepairAsync(nestedNotUpscaledDir, nestedUpscaledDir);

        _metadata
            .AnalyzePageDifferencesAsync(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new PageDifferenceResult(new[] { "002" }, Array.Empty<string>()));

        var processor = _app.Services.GetRequiredService<DistributedUpscaleTaskProcessor>();
        using (IServiceScope scope = _app.Services.CreateScope())
        {
            PersistedTask repairTask = await LoadTaskAsync(nestedTaskId);
            Assert.True(
                await processor.PrepareRepairTaskForRemote(
                    (RepairUpscaleTask)repairTask.Data,
                    repairTask,
                    scope.ServiceProvider,
                    CancellationToken.None
                )
            );
        }

        var client = new RemoteUpscalingServiceClient(_channel);
        var sut = CreatePageStreamClient(new FakeWorkerClient());

        await sut.RunAsync(client, nestedTaskId, _profile, TestContext.Current.CancellationToken);

        // The repaired page is merged as a top-level entry (the merge copies by file name); before
        // the flatten fix it was written as ch1/002.webp, missed by the non-recursive merge, and the
        // repair silently no-opped.
        using ZipArchive result = ZipFile.OpenRead(nestedUpscaled);
        Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(result, "002.webp"));
        Assert.NotNull(result.GetEntry("ch1/001.webp"));
    }

    private async Task<int> SeedNestedRepairAsync(string notUpscaledDir, string upscaledDir)
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync();

        var library = new Library
        {
            Name = "Nested Library",
            NotUpscaledLibraryPath = notUpscaledDir,
            UpscaledLibraryPath = upscaledDir,
        };
        var manga = new Manga { PrimaryTitle = "Nested Series", Library = library };
        var chapter = new Chapter
        {
            Manga = manga,
            FileName = "Chapter 2.cbz",
            RelativePath = Path.Combine("Series", "Chapter 2.cbz"),
        };
        context.AddRange(library, manga, chapter);
        await context.SaveChangesAsync();

        var task = new PersistedTask
        {
            Data = new RepairUpscaleTask
            {
                ChapterId = chapter.Id,
                UpscalerProfileId = _profile.Id,
            },
            Status = PersistedTaskStatus.Pending,
        };
        context.PersistedTasks.Add(task);
        await context.SaveChangesAsync();
        return task.Id;
    }

    private async Task<PersistedTask> LoadTaskAsync(int taskId)
    {
        await using ApplicationDbContext context = await _database.CreateContextAsync();
        return await context.PersistedTasks.FirstAsync(t => t.Id == taskId);
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

        var repairTask = new PersistedTask
        {
            Data = new RepairUpscaleTask
            {
                ChapterId = chapter.Id,
                UpscalerProfileId = _profile.Id,
            },
            Status = PersistedTaskStatus.Pending,
        };
        context.PersistedTasks.Add(repairTask);
        await context.SaveChangesAsync();

        _chapterId = chapter.Id;
        _taskId = task.Id;
        _detectTaskId = detectTask.Id;
        _repairTaskId = repairTask.Id;
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
