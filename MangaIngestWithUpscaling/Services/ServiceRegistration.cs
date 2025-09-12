using MangaIngestWithUpscaling.Services.Background;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Shared.Services;

namespace MangaIngestWithUpscaling.Services;

public static class ServiceRegistration
{
    public static void RegisterAppServices(this IServiceCollection services)
    {
        services.RegisterSharedServices();
        services.AutoRegister();

        services.RegisterIntegrations();

        services.AddSingleton<TaskQueue>();
        services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<TaskQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<TaskQueue>());
        services.AddSingleton<StandardTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<StandardTaskProcessor>());
        services.AddSingleton<UpscaleTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<UpscaleTaskProcessor>());
        services.AddSingleton<DistributedUpscaleTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<DistributedUpscaleTaskProcessor>());
        services.AddSingleton<PeriodicIngestWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<PeriodicIngestWatcher>());
        services.AddSingleton<LibraryIngestWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryIngestWatcher>());
        services.AddSingleton<PeriodicIntegrityChecker>();
        services.AddHostedService(sp => sp.GetRequiredService<PeriodicIntegrityChecker>());
        services.AddSingleton<PeriodicTaskReplayer>();
        services.AddHostedService(sp => sp.GetRequiredService<PeriodicTaskReplayer>());
        services.AddSingleton<TaskRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskRegistry>());
        services.AddScoped<ILibraryRenamingService, LibraryRenamingService>();
    }
}