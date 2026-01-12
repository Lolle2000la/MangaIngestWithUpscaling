namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface IQueueCleanup
{
    Task<IReadOnlyList<int>> CleanupAsync();
}
