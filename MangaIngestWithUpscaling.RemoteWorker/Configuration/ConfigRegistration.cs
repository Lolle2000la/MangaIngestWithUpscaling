using MangaIngestWithUpscaling.Shared.Configuration;
using System.Diagnostics.CodeAnalysis;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

public static class ConfigRegistration
{
    [RequiresDynamicCode("Binding strongly typed objects to configuration values may require generating dynamic code at runtime.")]
    [RequiresUnreferencedCode("TOptions's dependent types may have their members trimmed. Ensure all required members are preserved.")]
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<UpscalerConfig>(builder.Configuration.GetSection(UpscalerConfig.Position));
        builder.Services.Configure<UnixPermissionsConfig>(
            builder.Configuration.GetSection(UnixPermissionsConfig.Position));
        builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection(WorkerConfig.SectionName));
    }
}