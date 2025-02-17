using MangaIngestWithUpscaling.Services.Background;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.CbzConversion;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.Python;
using MangaIngestWithUpscaling.Services.Upscaling;
using System.Runtime.InteropServices;

namespace MangaIngestWithUpscaling.Services;

public static class ServiceRegistration
{
    public static void RegisterAppServices(this IServiceCollection services)
    {
        services.AutoRegister();

        // register unix file system if running on unix, otherwise use generic file system
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            services.AddSingleton<IFileSystem, UnixFileSystem>();
        }
        else
        {
            services.AddSingleton<IFileSystem, GenericFileSystem>();
        }

        services.AddSingleton<TaskQueue>();
        services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<TaskQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<TaskQueue>());
        services.AddSingleton<StandardTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<StandardTaskProcessor>());
        services.AddSingleton<UpscaleTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<UpscaleTaskProcessor>());
        services.AddSingleton<PeriodicChecker>();
        services.AddHostedService(sp => sp.GetRequiredService<PeriodicChecker>());
        services.AddSingleton<LibraryIngestWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryIngestWatcher>());
    }
}
