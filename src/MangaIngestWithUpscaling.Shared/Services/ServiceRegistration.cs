using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Shared.Services;

public static class ServiceRegistration
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AutoRegister();

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
