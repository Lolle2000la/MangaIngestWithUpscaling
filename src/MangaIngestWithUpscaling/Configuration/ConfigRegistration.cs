using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        builder
            .Services.AddOptions<QuietHoursConfig>()
            .Bind(builder.Configuration.GetSection(QuietHoursConfig.Position))
            .ValidateOnStart();
        // Same reason as the upscaler validator: a mistyped time of day must fail the boot rather
        // than silently leave the upscale queue running (or paused) at the wrong hour.
        builder.Services.AddSingleton<
            IValidateOptions<QuietHoursConfig>,
            QuietHoursConfigValidator
        >();
        builder.Services.AddSingleton<IQuietHoursGate, QuietHoursGate>();
        // Quiet hours is a local wall-clock window, so the gate needs a clock that knows the local
        // zone. TimeProvider is not registered elsewhere (AuthenticationHandler.TimeProvider is a
        // property of that handler, not a service).
        builder.Services.TryAddSingleton(TimeProvider.System);
    }
}
