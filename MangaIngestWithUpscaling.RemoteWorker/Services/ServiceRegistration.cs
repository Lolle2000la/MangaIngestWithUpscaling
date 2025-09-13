using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Services;

namespace MangaIngestWithUpscaling.RemoteWorker.Services;

public static class ServiceRegistration
{
    public static void RegisterRemoteWorkerServices(this IServiceCollection services)
    {
        services.RegisterSharedServices();
        services.AutoRegister();

        services.AddSingleton<RemoteTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<RemoteTaskProcessor>());
    }
}