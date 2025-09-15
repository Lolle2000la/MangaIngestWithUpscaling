using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public static class ConfigRegistration
{
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<UpscalerConfig>(builder.Configuration.GetSection(UpscalerConfig.Position));
        builder.Services.Configure<UnixPermissionsConfig>(
            builder.Configuration.GetSection(UnixPermissionsConfig.Position));
        builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection(WorkerConfig.SectionName));
    }
}