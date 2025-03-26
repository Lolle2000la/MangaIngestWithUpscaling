
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MangaIngestWithUpscaling.Services.Background;

public class LibraryIngestWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private readonly List<IDisposable> fileSystemWatchers = new();

    public LibraryIngestWatcher(IServiceScopeFactory serviceScopeFactory) => _serviceScopeFactory = serviceScopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RegisterFileWatchers(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1500, stoppingToken);
        }

        UnregisterWatchers();
    }

    private void RegisterFileWatchers(CancellationToken stoppingToken)
    {
        lock (fileSystemWatchers)
        {
            if (fileSystemWatchers.Count == 0)
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var libraries = dbContext.Libraries.ToList();
                foreach (var library in libraries)
                {
                    var watcher = new FileSystemWatcher(library.IngestPath)
                    {
                        EnableRaisingEvents = true,
                        IncludeSubdirectories = true,
                        Filters = { "*.cbz", "ComicInfo.xml" }
                    };

                    var compositeDisposable = new CompositeDisposable();
                    compositeDisposable.Add(watcher);

                    Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
                        h => watcher.Created += h,
                        h => watcher.Created -= h)
                        .Throttle(TimeSpan.FromSeconds(5))
                        .Subscribe(async e =>
                        {
                            // process the new file
                            using var scope = _serviceScopeFactory.CreateScope();
                            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var taskQueue = scope.ServiceProvider.GetRequiredService<ITaskQueue>();
                            // ensure the library still exists
                            if (await dbContext.Libraries.AnyAsync(l => l.Id == library.Id, stoppingToken))
                                await taskQueue.EnqueueAsync(new ScanIngestTask() { LibraryId = library.Id });
                        })
                        .DisposeWith(compositeDisposable);

                    fileSystemWatchers.Add(compositeDisposable);
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
        foreach (var watcher in fileSystemWatchers)
        {
            watcher.Dispose();
        }
    }
}
