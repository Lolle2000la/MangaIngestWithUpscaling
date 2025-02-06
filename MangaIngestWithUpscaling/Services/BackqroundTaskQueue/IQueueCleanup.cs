namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public interface IQueueCleanup
{
    Task CleanupAsync();
}
