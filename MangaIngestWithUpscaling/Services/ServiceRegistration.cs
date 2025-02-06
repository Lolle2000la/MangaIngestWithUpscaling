﻿using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.CbzConversion;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.Python;
using MangaIngestWithUpscaling.Services.Upscaling;

namespace MangaIngestWithUpscaling.Services;

public static class ServiceRegistration
{
    public static void RegisterAppServices(this IServiceCollection services)
    {
        services.AddScoped<IChapterInIngestRecognitionService, ChapterInIngestRecognitionService>();
        services.AddScoped<IChapterDeletion, ChapterDeletion>();
        services.AddScoped<IMetadataHandlingService, MetadataHandlingService>();
        services.AddScoped<ILibraryFilteringService, LibraryFilteringService>();
        services.AddScoped<ICbzConverter, CbzConverter>();
        services.AddScoped<IPythonService, PythonService>();
        services.AddScoped<IIngestProcessor, IngestProcessor>();
        services.AddScoped<IUpscaler, MangaJaNaiUpscaler>();
        services.AddScoped<IMangaMetadataChanger, MangaMetadataChanger>();
        services.AddScoped<IQueueCleanup, QueueCleanup>();

        services.AddSingleton<TaskQueue>();
        services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<TaskQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<TaskQueue>());
        services.AddSingleton<StandardTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<StandardTaskProcessor>());
        services.AddSingleton<UpscaleTaskProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<UpscaleTaskProcessor>());
    }
}
