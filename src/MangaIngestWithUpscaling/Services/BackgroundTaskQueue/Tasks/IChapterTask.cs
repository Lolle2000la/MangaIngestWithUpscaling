namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Represents a background task that is associated with a specific chapter.
/// </summary>
public interface IChapterTask
{
    int ChapterId { get; }
}
