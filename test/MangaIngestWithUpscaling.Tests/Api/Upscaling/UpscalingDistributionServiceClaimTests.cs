using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.RepairServices;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Api.Upscaling;

/// <summary>
/// Regression tests for the claim RPC. A task is transitioned to Processing by
/// <see cref="DistributedUpscaleTaskProcessor.GetTask" /> before the service can validate that it is
/// actually delegatable, so a validation failure must dispose of the claimed task instead of leaving
/// it for the dead-task reaper to requeue forever.
/// </summary>
public sealed class UpscalingDistributionServiceClaimTests : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _serviceScope;
    private readonly ApplicationDbContext _serviceDbContext;
    private readonly TaskQueue _taskQueue;
    private readonly DistributedUpscaleTaskProcessor _processor;
    private readonly string _root;
    private readonly int _chapterId;

    public UpscalingDistributionServiceClaimTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _database = TestDatabaseFactory.Create();
        using (ApplicationDbContext schema = _database.CreateContext()) { }
        services.AddDbContext<ApplicationDbContext>(options => _database.Configure(options));

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = true })
        );
        services.AddScoped(_ => Substitute.For<IRepairService>());
        services.AddScoped(_ => Substitute.For<IMetadataHandlingService>());

        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope
                .ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
        }

        // A real chapter with a real source archive, so the processor's pre-handoff checks pass and
        // the task actually reaches the claim RPC's profile lookup.
        _root = Directory.CreateTempSubdirectory("distribution_claim").FullName;
        using (ApplicationDbContext ctx = _database.CreateContext())
        {
            var library = new Library
            {
                Name = "Lib",
                NotUpscaledLibraryPath = Path.Combine(_root, "orig"),
                UpscaledLibraryPath = Path.Combine(_root, "up"),
            };
            Directory.CreateDirectory(library.NotUpscaledLibraryPath);
            Directory.CreateDirectory(library.UpscaledLibraryPath!);
            var manga = new Manga { PrimaryTitle = "Series", Library = library };
            var chapter = new Chapter
            {
                FileName = "ch1.cbz",
                RelativePath = "Series/Ch1.cbz",
                Manga = manga,
                IsUpscaled = false,
            };
            manga.Chapters.Add(chapter);
            library.MangaSeries.Add(manga);
            ctx.Libraries.Add(library);
            ctx.SaveChanges();
            _chapterId = chapter.Id;

            string sourcePath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "source");
        }

        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _taskQueue = new TaskQueue(
            scopeFactory,
            _provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        _processor = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            scopeFactory,
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            new TaskPersistenceService(scopeFactory)
        );

        _serviceScope = _provider.CreateScope();
        _serviceDbContext =
            _serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public async ValueTask DisposeAsync()
    {
        _serviceScope.Dispose();
        await _provider.DisposeAsync();
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
    [Trait("Category", "Unit")]
    public async Task RequestUpscaleTask_WhenClaimedTaskProfileIsMissing_FailsTheTaskInsteadOfStrandingIt()
    {
        // A task whose upscaler profile no longer exists cannot be delegated, but it is claimed
        // (Processing, tracked as running) before the service notices. Returning "no task" without
        // failing it would leave it to the dead-task reaper, which requeues it without ever
        // consuming its retry budget, so the task would loop forever: never completed, never failed,
        // never surfaced. The service must fail the claimed task instead.
        var serviceCts = new CancellationTokenSource();
        await _taskQueue.EnqueueAsync(
            new UpscaleTask { ChapterId = _chapterId, UpscalerProfileId = 999_999 }
        );
        int taskId = _taskQueue.GetUpscaleSnapshot().Single().Id;

        await _processor.StartAsync(serviceCts.Token);
        try
        {
            UpscalingDistributionService service = CreateService();

            UpscaleTaskDelegationResponse response = await service.RequestUpscaleTaskWithHint(
                new RequestTaskRequest(),
                Substitute.For<ServerCallContext>()
            );

            Assert.Equal(-1, response.TaskId);
            Assert.Equal(PersistedTaskStatus.Failed, await GetStatusAsync(taskId));
            Assert.False(_processor.IsRunningRemotely(taskId));
        }
        finally
        {
            await serviceCts.CancelAsync();
            await _processor.StopAsync(CancellationToken.None);
        }
    }

    private UpscalingDistributionService CreateService() =>
        new(
            _processor,
            _serviceDbContext,
            Substitute.For<IFileSystem>(),
            Substitute.For<IChapterChangedNotifier>(),
            Substitute.For<ISplitProcessingService>(),
            Substitute.For<IPageSpoolStore>(),
            new PageContextCache(),
            Substitute.For<IUpscalerJsonHandlingService>(),
            Substitute.For<IMetadataHandlingService>(),
            Substitute.For<IImageResizeService>(),
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            Substitute.For<ILogger<UpscalingDistributionService>>()
        );

    private async Task<PersistedTaskStatus?> GetStatusAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? task = await db
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, TestContext.Current.CancellationToken);
        return task?.Status;
    }
}
