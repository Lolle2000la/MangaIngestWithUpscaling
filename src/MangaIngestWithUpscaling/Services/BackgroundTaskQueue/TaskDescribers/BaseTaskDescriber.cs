using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

public abstract class BaseTaskDescriber<T> : ITaskDescriber<BaseTask>
    where T : BaseTask
{
    protected readonly IStringLocalizer<TaskStrings> Localizer;

    protected BaseTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    {
        Localizer = localizer;
    }

    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is T t)
        {
            return GetTitleAsync(t);
        }
        return Task.FromResult(string.Empty);
    }

    public Task<string> GetDescriptionAsync(BaseTask task)
    {
        if (task is T t)
        {
            return GetDescriptionAsync(t);
        }
        return Task.FromResult(string.Empty);
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (task is T t)
        {
            return GetProgressStatusAsync(t, progress);
        }
        return Task.FromResult(string.Empty);
    }

    public abstract Task<string> GetTitleAsync(T task);

    public virtual Task<string> GetDescriptionAsync(T task) => Task.FromResult(string.Empty);

    public virtual Task<string> GetProgressStatusAsync(T task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            // e.g. "Processed: 5"
            return Task.FromResult(Localizer["Progress_Indeterminate", progress.Current].Value);
        }
        // e.g. "5 / 10" or "50%"
        return Task.FromResult(
            Localizer["Progress_Determinate", progress.Current, progress.Total].Value
        );
    }
}
