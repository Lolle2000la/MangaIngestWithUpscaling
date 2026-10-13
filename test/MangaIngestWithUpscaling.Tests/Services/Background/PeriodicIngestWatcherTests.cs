using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Background;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.Background;

public class PeriodicIngestWatcherTests
{
    private static ApplicationDbContext CreateInMemoryContext(string databaseName) =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options
        );

    [Fact]
    public async Task RunOnceAsync_TransientDatabaseFailure_IsLoggedAndSwallowed()
    {
        // A real context that is already disposed makes any query throw, standing in for a momentary
        // SQLite lock or PostgreSQL restart.
        ApplicationDbContext disposedContext = CreateInMemoryContext(
            $"periodic-watcher-throw-{Guid.NewGuid():N}"
        );
        disposedContext.Dispose();

        var ingestProcessor = Substitute.For<IIngestProcessor>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ApplicationDbContext)).Returns(disposedContext);
        provider.GetService(typeof(IIngestProcessor)).Returns(ingestProcessor);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var logger = Substitute.For<ILogger<PeriodicIngestWatcher>>();
        var watcher = new PeriodicIngestWatcher(scopeFactory, logger);

        // A transient failure must not escape RunOnceAsync: under the default StopHost behavior an
        // escaping exception would take the whole web host down. It is logged instead.
        await watcher.RunOnceAsync(CancellationToken.None);

        Assert.Contains(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
    }

    [Fact]
    public async Task RunOnceAsync_ShutdownCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new OperationCanceledException(cts.Token));

        var watcher = new PeriodicIngestWatcher(
            scopeFactory,
            NullLogger<PeriodicIngestWatcher>.Instance
        );

        // Shutdown must stay prompt: a cancellation tied to the stopping token still escapes.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            watcher.RunOnceAsync(cts.Token)
        );
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesWatcher_WhenAnIngestPathIsAdded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TestDatabase database = TestDatabaseFactory.Create();

        var ingestProcessor = Substitute.For<IIngestProcessor>();

        // Observe each re-registration request without depending on real FileSystemWatchers.
        var notifyScopeFactory = new ThrowingScopeFactory();
        var libraryWatcher = new LibraryIngestWatcher(
            notifyScopeFactory,
            NullLogger<LibraryIngestWatcher>.Instance
        );

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => database.Configure(options));
        services.AddScoped(_ => ingestProcessor);
        services.AddSingleton(libraryWatcher);
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using (ApplicationDbContext dbContext = await database.CreateContextAsync(ct))
        {
            dbContext.Libraries.Add(
                new Library
                {
                    Name = "Library",
                    NotUpscaledLibraryPath = "/tmp/not-upscaled",
                    IngestPaths = { new LibraryIngestPath { Path = "/tmp/ingest-a" } },
                }
            );
            await dbContext.SaveChangesAsync(ct);
        }

        var watcher = new PeriodicIngestWatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PeriodicIngestWatcher>.Instance
        );

        await watcher.RunOnceAsync(ct);
        int afterFirst = notifyScopeFactory.CreateScopeCount;

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Library library = await dbContext.Libraries.SingleAsync(ct);
            library.IngestPaths.Add(new LibraryIngestPath { Path = "/tmp/ingest-b" });
            await dbContext.SaveChangesAsync(ct);
        }

        await watcher.RunOnceAsync(ct);

        // The first check notifies because no libraries were known yet; the second must notify again
        // because an ingest path was added. Without Include(IngestPaths) the comparison sees two
        // empty collections and the change goes unnoticed.
        Assert.Equal(afterFirst + 1, notifyScopeFactory.CreateScopeCount);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public int CreateScopeCount { get; private set; }

        public IServiceScope CreateScope()
        {
            CreateScopeCount++;
            // RegisterFileWatchers only needs a database, which this test does not provide; fail fast
            // so the test observes just the re-registration request. RunOnceAsync swallows the throw.
            throw new InvalidOperationException("re-register");
        }
    }
}
