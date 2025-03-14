using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrety;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class PeriodicChecker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    public PeriodicChecker(IServiceScopeFactory serviceScopeFactory) => _serviceScopeFactory = serviceScopeFactory;

    private List<Library> libraries = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<PeriodicChecker>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var taskQueue = scope.ServiceProvider.GetRequiredService<TaskQueue>();
            await taskQueue.ReplayPendingOrFailed();

            var ingestProcessor = scope.ServiceProvider.GetRequiredService<IIngestProcessor>();

            var libraries = await dbContext.Libraries.ToListAsync(stoppingToken);
            foreach (var library in libraries)
            {
                try
                {
                    await ingestProcessor.ProcessAsync(library, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error processing library {library.Name}");
                }
            }

            // check if the libraries have changed and update the list
            var newLibraries = await dbContext.Libraries.ToListAsync(stoppingToken);
            if (!libraries.SequenceEqual(newLibraries))
            {
                libraries = newLibraries;
                var ingestWatcher = scope.ServiceProvider.GetRequiredService<LibraryIngestWatcher>();
                ingestWatcher.NotifyLibrariesHaveChanged();
            }

            // Ensure the library integrity. This will at this point primarily check for missing files.
            var libraryIntegrityChecker = scope.ServiceProvider.GetRequiredService<ILibraryIntegrityChecker>();
            await libraryIntegrityChecker.CheckIntegrity(stoppingToken);
        }
    }
}
