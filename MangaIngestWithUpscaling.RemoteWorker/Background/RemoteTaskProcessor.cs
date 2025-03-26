using MangaIngestWithUpscaling.Api.Upscaling;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = serviceScopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();


        }
    }
}
