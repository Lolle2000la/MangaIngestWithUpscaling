namespace MangaIngestWithUpscaling.Configuration;

public static class ConfigRegistration
{
    public static void RegisterConfig(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<UpscalerConfig>(builder.Configuration.GetSection(UpscalerConfig.Position));
        builder.Services.Configure<KavitaConfiguration>(builder.Configuration.GetSection(KavitaConfiguration.Position));
    }
}
