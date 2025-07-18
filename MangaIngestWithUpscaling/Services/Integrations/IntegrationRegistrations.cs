﻿using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Services.Integrations.Kavita;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Integrations;

public static class IntegrationRegistrations
{
    public static IServiceCollection RegisterIntegrations(this IServiceCollection services)
    {
        services.AddScoped<IChapterChangedNotifier, KavitaChapterChangedNotifier>();

        services.AddHttpClient<IKavitaClient, KavitaClient>((provider, client) =>
            {
                var config = provider.GetRequiredService<IOptions<KavitaConfiguration>>().Value;
                if (!string.IsNullOrWhiteSpace(config.BaseUrl))
                    client.BaseAddress = new Uri(config.BaseUrl);
            })
            .AddStandardResilienceHandler();

        return services;
    }
}