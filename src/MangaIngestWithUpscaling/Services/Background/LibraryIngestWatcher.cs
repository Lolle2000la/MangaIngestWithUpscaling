using System.Reactive.Disposables;
using System.Reactive.Linq;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class LibraryIngestWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<LibraryIngestWatcher> _logger;

    private readonly List<IDisposable> fileSystemWatchers = new();

    public LibraryIngestWatcher(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<LibraryIngestWatcher> logger
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            RegisterFileWatchers(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1500, stoppingToken);
            }
        }
        finally
        {
            // The delay throws OperationCanceledException on shutdown, so a try/finally
            // guarantees the FileSystemWatchers are disposed instead of leaking — including
            // when RegisterFileWatchers fails partway through.
            UnregisterWatchers();
        }
    }

    private void RegisterFileWatchers(CancellationToken stoppingToken)
    {
        lock (fileSystemWatchers)
        {
            if (fileSystemWatchers.Count == 0)
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var libraries = dbContext.Libraries.Include(l => l.IngestPaths).ToList();
                foreach (var library in libraries)
                {
                    foreach (var ingestPath in library.IngestPaths)
                    {
                        string path = ingestPath.Path;
                        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        {
                            continue;
                        }

                        var watcher = new FileSystemWatcher(path)
                        {
                            EnableRaisingEvents = true,
                            IncludeSubdirectories = true,
                            Filters = { "*.cbz", "ComicInfo.xml" },
                        };

                        var compositeDisposable = new CompositeDisposable();
                        compositeDisposable.Add(watcher);

                        Observable
                            .FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
                                h => watcher.Created += h,
                                h => watcher.Created -= h
                            )
                            .Throttle(TimeSpan.FromSeconds(15))
                            .Subscribe(async e =>
                            {
                                // Subscribe(Action<...>) makes this handler async void, so an
                                // exception after the first await would be unobserved and can tear
                                // down the process. Observe and log everything instead.
                                try
                                {
                                    // process the new file
                                    using var scope = _serviceScopeFactory.CreateScope();
                                    var dbContext =
                                        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                    var taskQueue =
                                        scope.ServiceProvider.GetRequiredService<ITaskQueue>();
                                    // ensure the library still exists
                                    if (
                                        await dbContext.Libraries.AnyAsync(
                                            l => l.Id == library.Id,
                                            stoppingToken
                                        )
                                    )
                                        await taskQueue.EnqueueAsync(
                                            new ScanIngestTask() { LibraryId = library.Id }
                                        );
                                }
                                catch (OperationCanceledException)
                                {
                                    // Host is shutting down; nothing to do.
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(
                                        ex,
                                        "Failed to handle a new file event for library {LibraryId}",
                                        library.Id
                                    );
                                }
                            })
                            .DisposeWith(compositeDisposable);

                        fileSystemWatchers.Add(compositeDisposable);
                    }
                }
            }
        }
    }

    public void NotifyLibrariesHaveChanged()
    {
        UnregisterWatchers();
        RegisterFileWatchers(CancellationToken.None);
    }

    private void UnregisterWatchers()
    {
        lock (fileSystemWatchers)
        {
            foreach (var watcher in fileSystemWatchers)
            {
                watcher.Dispose();
            }

            fileSystemWatchers.Clear();
        }
    }
}
