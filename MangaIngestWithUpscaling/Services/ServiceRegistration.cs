using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.MetadataExtraction;

namespace MangaIngestWithUpscaling.Services
{
    public static class ServiceRegistration
    {
        public static void RegisterAppServices(this IServiceCollection services)
        {
            services.AddScoped<IChapterInIngestRecognitionService, ChapterInIngestRecognitionService>();
            services.AddScoped<IMetadataExtractionService, MetadataExtractionService>();
        }
    }
}
