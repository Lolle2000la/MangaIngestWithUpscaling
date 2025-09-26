using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface IQueueCleanup
{
    Task CleanupAsync(ApplicationDbContext context);
}
