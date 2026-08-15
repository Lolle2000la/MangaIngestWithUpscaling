using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Shared.Services;

public static class ServiceRegistration
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AutoRegister();

        // The worker client is a single long-lived process manager shared by every upscale job.
        services.AddSingleton<MangaJaNaiWorkerClient>();
        services.AddSingleton<IMangaJaNaiWorkerClient>(sp =>
            sp.GetRequiredService<MangaJaNaiWorkerClient>()
        );
        services.AddHostedService(sp => sp.GetRequiredService<MangaJaNaiWorkerClient>());

        // register unix file system if running on unix, otherwise use generic file system
        if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)
        )
        {
            services.AddSingleton<IFileSystem, UnixFileSystem>();
        }
        else
        {
            services.AddSingleton<IFileSystem, GenericFileSystem>();
        }
    }
}
