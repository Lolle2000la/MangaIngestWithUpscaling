using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public static class ConfigRegistration
{
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<UpscalerConfig>()
            .Bind(builder.Configuration.GetSection(UpscalerConfig.Position))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<UpscalerConfig>, UpscalerConfigValidator>();
        builder.Services.Configure<UnixPermissionsConfig>(
            builder.Configuration.GetSection(UnixPermissionsConfig.Position)
        );
        builder.Services.Configure<WorkerConfig>(
            builder.Configuration.GetSection(WorkerConfig.SectionName)
        );
    }
}
