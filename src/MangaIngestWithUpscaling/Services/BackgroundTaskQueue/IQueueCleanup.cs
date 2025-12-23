namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface IQueueCleanup
{
    Task CleanupAsync();
}
