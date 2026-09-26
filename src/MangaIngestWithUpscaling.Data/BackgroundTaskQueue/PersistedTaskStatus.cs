namespace MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

public enum PersistedTaskStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Canceled,
}
