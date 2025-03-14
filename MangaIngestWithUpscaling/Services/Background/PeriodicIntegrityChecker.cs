using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class PeriodicIntegrityChecker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    public PeriodicIntegrityChecker(IServiceScopeFactory serviceScopeFactory) => _serviceScopeFactory = serviceScopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(3));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<PeriodicIntegrityChecker>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<LibraryIntegrityChecker>();

            // Ensure the library integrity. This will at this point primarily check for missing files.
            var libraryIntegrityChecker = scope.ServiceProvider.GetRequiredService<ILibraryIntegrityChecker>();
            try
            {
                await libraryIntegrityChecker.CheckIntegrity(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking library integrity");
            }
        }
    }
}
