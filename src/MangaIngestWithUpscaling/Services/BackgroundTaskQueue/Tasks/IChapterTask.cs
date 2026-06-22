namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public interface IChapterTask
{
    int ChapterId { get; }
}
