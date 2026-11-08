using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // The worker's own half of quiet hours: it stops requesting new upscale tasks, independently
        // of the server's window. The server's window never blocks this worker from fetching.
        builder
            .Services.AddOptions<QuietHoursConfig>()
            .Bind(builder.Configuration.GetSection(QuietHoursConfig.Position))
            .ValidateOnStart();
        builder.Services.AddSingleton<
            IValidateOptions<QuietHoursConfig>,
            QuietHoursConfigValidator
        >();
        builder.Services.AddSingleton<IQuietHoursGate, QuietHoursGate>();
        // The window is local wall-clock time, so the gate needs a clock that knows the local zone.
        builder.Services.TryAddSingleton(TimeProvider.System);
    }
}
