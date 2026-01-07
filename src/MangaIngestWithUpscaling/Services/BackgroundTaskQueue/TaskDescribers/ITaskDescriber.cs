using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

public interface ITaskDescriber<in T>
    where T : BaseTask
{
    Task<string> GetTitleAsync(T task);
    Task<string> GetDescriptionAsync(T task);
    Task<string> GetProgressStatusAsync(T task, ProgressInfo progress);
}
