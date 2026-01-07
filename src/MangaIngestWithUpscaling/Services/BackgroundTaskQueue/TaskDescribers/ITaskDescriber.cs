using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

public interface ITaskDescriber<in T>
    where T : BaseTask
{
    public Task<string> GetTitleAsync(T task);
    public Task<string> GetProgressStatusAsync(T task, ProgressInfo progress) =>
        Task.FromResult(string.Empty);
}
