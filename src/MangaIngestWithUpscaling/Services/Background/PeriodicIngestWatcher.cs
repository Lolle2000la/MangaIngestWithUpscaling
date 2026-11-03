using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class PeriodicIngestWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PeriodicIngestWatcher> _logger;

    public PeriodicIngestWatcher(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PeriodicIngestWatcher> logger
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    private List<Library> libraries = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs a single periodic check: process every library and re-register the filesystem watchers
    /// when the library configuration changed.
    ///
    /// A transient database or processing failure is logged and swallowed here instead of escaping
    /// <see cref="ExecuteAsync"/>: the default
    /// <c>HostOptions.BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so one momentary
    /// SQLite lock or PostgreSQL restart would otherwise take the whole web host down. Cancellation
    /// is rethrown so shutdown stays prompt.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var ingestProcessor = scope.ServiceProvider.GetRequiredService<IIngestProcessor>();

            // IngestPaths are configuration and Library.Equals compares them; without the Include
            // both snapshots would compare two empty collections and an added/removed/edited ingest
            // path would go unnoticed (and never re-register the watchers).
            var libraries = await dbContext
                .Libraries.Include(l => l.IngestPaths)
                .ToListAsync(stoppingToken);
            foreach (var library in libraries)
            {
                try
                {
                    await ingestProcessor.ProcessAsync(library, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing library {library.Name}");
                }
            }

            // check if the libraries have changed and update the list
            var newLibraries = await dbContext
                .Libraries.Include(l => l.IngestPaths)
                .ToListAsync(stoppingToken);
            if (!this.libraries.SequenceEqual(newLibraries))
            {
                this.libraries = newLibraries;
                var ingestWatcher =
                    scope.ServiceProvider.GetRequiredService<LibraryIngestWatcher>();
                ingestWatcher.NotifyLibrariesHaveChanged();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic ingest check failed; retrying on the next tick.");
        }
    }
}
