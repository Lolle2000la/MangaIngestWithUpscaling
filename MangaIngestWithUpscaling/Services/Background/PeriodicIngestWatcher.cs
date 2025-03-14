using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class PeriodicIngestWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    public PeriodicIngestWatcher(IServiceScopeFactory serviceScopeFactory) => _serviceScopeFactory = serviceScopeFactory;

    private List<Library> libraries = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<PeriodicIngestWatcher>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var ingestProcessor = scope.ServiceProvider.GetRequiredService<IIngestProcessor>();

            var libraries = await dbContext.Libraries.ToListAsync(stoppingToken);
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
                    logger.LogError(ex, $"Error processing library {library.Name}");
                }
            }

            // check if the libraries have changed and update the list
            var newLibraries = await dbContext.Libraries.ToListAsync(stoppingToken);
            if (!this.libraries.SequenceEqual(newLibraries))
            {
                this.libraries = newLibraries;
                var ingestWatcher = scope.ServiceProvider.GetRequiredService<LibraryIngestWatcher>();
                ingestWatcher.NotifyLibrariesHaveChanged();
            }
        }
    }
}
