using MangaIngestWithUpscaling.Shared.Configuration;
using System.Diagnostics.CodeAnalysis;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public static class ConfigRegistration
{
    [RequiresDynamicCode("Configuration binding may require dynamic code")]
    [RequiresUnreferencedCode("Configuration binding may require unreferenced code")]
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<UpscalerConfig>(builder.Configuration.GetSection(UpscalerConfig.Position));
        builder.Services.Configure<UnixPermissionsConfig>(
            builder.Configuration.GetSection(UnixPermissionsConfig.Position));
        builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection(WorkerConfig.SectionName));
    }
}