using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
///     SQLite is used because the rename task includes <c>Library</c>, whose complex type the
///     in-memory provider cannot shape.
/// </summary>
public class RemoteOnlyReroutedTaskTests : IDisposable
{
    private readonly string _dbFile;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskQueue _taskQueue;
    private readonly UpscaleTaskProcessor _local;
    private readonly DistributedUpscaleTaskProcessor _distributed;
    private readonly int _chapterId;

    public RemoteOnlyReroutedTaskTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _dbFile = Path.Combine(Path.GetTempPath(), $"remote-only-reroute-{Guid.NewGuid():N}.db");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={_dbFile}")
        );
        services.AddSingleton<IOptions<UpscalerConfig>>(
            Options.Create(new UpscalerConfig { RemoteOnly = true })
        );

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);

        // The rename task resolves these before the IsUpscaled short-circuit, so they must exist.
        services.AddSingleton(Substitute.For<IStringLocalizer<RenameUpscaledChaptersSeriesTask>>());
        services.AddSingleton(Substitute.For<IFileSystem>());

        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope
                .ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
        }

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        var persistence = new TaskPersistenceService(_scopeFactory);
        _taskQueue = new TaskQueue(
            _scopeFactory,
            _provider.GetRequiredService<ILogger<TaskQueue>>()
        );
        _local = new UpscaleTaskProcessor(
            _taskQueue,
            _scopeFactory,
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<UpscaleTaskProcessor>>(),
            persistence,
            new PreprocessedInputCache()
        );
        _distributed = new DistributedUpscaleTaskProcessor(
            _taskQueue,
            _scopeFactory,
            _provider.GetRequiredService<IOptions<UpscalerConfig>>(),
            _provider.GetRequiredService<ILogger<DistributedUpscaleTaskProcessor>>(),
            persistence
        );

        using var seedScope = _scopeFactory.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var library = new Library
        {
            Name = "RemoteOnlyLib",
            NotUpscaledLibraryPath = "/tmp/remote-only/not-upscaled",
            UpscaledLibraryPath = "/tmp/remote-only/upscaled",
        };
        var manga = new Manga { PrimaryTitle = "RemoteOnlyManga", Library = library };
        var chapter = new Chapter
        {
            Manga = manga,
            FileName = "Chapter 1.cbz",
            RelativePath = "Chapter 1.cbz",
            IsUpscaled = false,
        };
        db.Libraries.Add(library);
        db.MangaSeries.Add(manga);
        db.Chapters.Add(chapter);
        db.SaveChanges();
        _chapterId = chapter.Id;
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbFile);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp database.
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RenameTask_ReroutedInRemoteOnly_ReachesTerminalState()
    {
        // Regression guard: the distributed processor reroutes RenameUpscaledChaptersSeriesTask to
        // the local processor, which used to be disabled entirely in RemoteOnly. The task then sat
        // in Processing forever. It must now be executed and reach a terminal state.
        await _taskQueue.EnqueueAsync(
            new RenameUpscaledChaptersSeriesTask(
                _chapterId,
                "/tmp/remote-only/upscaled/Chapter 1.cbz",
                "RemoteOnlyManga"
            )
        );
        // A second, remotely-servable task so GetTask returns quickly instead of timing out.
        await _taskQueue.EnqueueAsync(new DetectSplitCandidatesTask(1, 1));

        int renameTaskId = _taskQueue
            .GetUpscaleSnapshot()
            .Single(t => t.Data is RenameUpscaledChaptersSeriesTask)
            .Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _local.StartAsync(cts.Token);
        await _distributed.StartAsync(cts.Token);

        // Act: a remote worker request drives the distributed processor to claim the rename task,
        // which reroutes it to the (RemoteOnly) local processor.
        PersistedTask? handedToRemote = await _distributed.GetTask(cts.Token);

        // Assert: the rename task reached a terminal state instead of staying Processing.
        Assert.NotNull(handedToRemote);
        Assert.IsType<DetectSplitCandidatesTask>(handedToRemote!.Data);

        PersistedTaskStatus? status = null;
        for (int i = 0; i < 100; i++)
        {
            status = await GetStatusAsync(renameTaskId);
            if (status != PersistedTaskStatus.Processing)
            {
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(PersistedTaskStatus.Completed, status);

        await _local.StopAsync(CancellationToken.None);
        await _distributed.StopAsync(CancellationToken.None);
    }

    private async Task<PersistedTaskStatus?> GetStatusAsync(int taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PersistedTask? task = await db
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
        return task?.Status;
    }
}
