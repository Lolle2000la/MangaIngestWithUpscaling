using Google.Protobuf.WellKnownTypes;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

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

            var taskResponse = await client.RequestUpscaleTaskAsync(new Empty(), cancellationToken: stoppingToken);

            if (taskResponse.TaskId == -1)
            {
                continue;
            }

            var profile = GetProfileFromResponse(taskResponse.UpscalerProfile);
        }
    }

    private static Shared.Data.LibraryManagement.UpscalerProfile GetProfileFromResponse(Api.Upscaling.UpscalerProfile upscalerProfile)
    {
        return new Shared.Data.LibraryManagement.UpscalerProfile()
        {
            CompressionFormat = upscalerProfile.CompressionFormat switch
            {
                Api.Upscaling.CompressionFormat.Webp => Shared.Data.LibraryManagement.CompressionFormat.Webp,
                Api.Upscaling.CompressionFormat.Png => Shared.Data.LibraryManagement.CompressionFormat.Png,
                Api.Upscaling.CompressionFormat.Jpg => Shared.Data.LibraryManagement.CompressionFormat.Jpg,
                Api.Upscaling.CompressionFormat.Avif => Shared.Data.LibraryManagement.CompressionFormat.Avif,
                _ => throw new InvalidOperationException("Unknown compression format.")
            },
            Name = upscalerProfile.Name,
            Quality = upscalerProfile.Quality,
            ScalingFactor = upscalerProfile.ScalingFactor switch
            {
                Api.Upscaling.ScaleFactor.OneX => Shared.Data.LibraryManagement.ScaleFactor.OneX,
                Api.Upscaling.ScaleFactor.TwoX => Shared.Data.LibraryManagement.ScaleFactor.TwoX,
                Api.Upscaling.ScaleFactor.ThreeX => Shared.Data.LibraryManagement.ScaleFactor.ThreeX,
                Api.Upscaling.ScaleFactor.FourX => Shared.Data.LibraryManagement.ScaleFactor.FourX,
                _ => throw new InvalidOperationException("Unknown scaling factor.")
            },
            UpscalerMethod = upscalerProfile.UpscalerMethod switch
            {
                Api.Upscaling.UpscalerMethod.MangaJaNai => Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai,
                _ => throw new InvalidOperationException("Unknown upscaler method.")
            }
        };
    }
}
