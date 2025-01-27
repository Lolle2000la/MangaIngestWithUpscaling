using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using MangaIngestWithUpscaling.Services.MetadataExtraction;

namespace MangaIngestWithUpscaling.Services
{
    public static class ServiceRegistration
    {
        public static void RegisterAppServices(this IServiceCollection services)
        {
            services.AddScoped<IChapterInIngestRecognitionService, ChapterInIngestRecognitionService>();
            services.AddScoped<IMetadataExtractionService, MetadataExtractionService>();
            services.AddScoped<ILibraryFilteringService, LibraryFilteringService>();

            services.AddSingleton<TaskQueue>();
            services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<TaskQueue>());
            services.AddHostedService(sp => sp.GetRequiredService<TaskQueue>());
            services.AddSingleton<StandardTaskProcessor>();
            services.AddHostedService(sp => sp.GetRequiredService<StandardTaskProcessor>());
            services.AddSingleton<UpscaleTaskProcessor>();
            services.AddHostedService(sp => sp.GetRequiredService<UpscaleTaskProcessor>());
        }
    }
}
