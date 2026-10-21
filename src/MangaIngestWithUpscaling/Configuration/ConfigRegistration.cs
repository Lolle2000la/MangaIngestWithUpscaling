using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Configuration;

public static class ConfigRegistration
{
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<UpscalerConfig>()
            .Bind(builder.Configuration.GetSection(UpscalerConfig.Position))
            .ValidateOnStart();
        // Size settings are strings so they can be written as "4GiB"; a value that parses to
        // nothing would otherwise silently drop an explicit budget.
        builder.Services.AddSingleton<IValidateOptions<UpscalerConfig>, UpscalerConfigValidator>();
        builder.Services.Configure<KavitaConfiguration>(
            builder.Configuration.GetSection(KavitaConfiguration.Position)
        );
        builder.Services.Configure<UnixPermissionsConfig>(
            builder.Configuration.GetSection(UnixPermissionsConfig.Position)
        );
        builder.Services.Configure<IntegrityCheckerConfig>(
            builder.Configuration.GetSection(IntegrityCheckerConfig.Position)
        );
    }
}
