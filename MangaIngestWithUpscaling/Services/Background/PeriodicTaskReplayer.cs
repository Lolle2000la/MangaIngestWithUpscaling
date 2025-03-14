using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrety;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Background;

public class PeriodicTaskReplayer : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    public PeriodicTaskReplayer(IServiceScopeFactory serviceScopeFactory) => _serviceScopeFactory = serviceScopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<PeriodicIntegrityChecker>>();

            var taskQueue = scope.ServiceProvider.GetRequiredService<TaskQueue>();
            try
            {
                await taskQueue.ReplayPendingOrFailed(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error replaying tasks");
            }
        }
    }
}
