using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(LibraryIntegrityCheckTaskDescriber))]
public class LibraryIntegrityCheckTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is LibraryIntegrityCheckTask t)
        {
            return Task.FromResult(
                localizer["Title_LibraryIntegrityCheckTask", t.LibraryName ?? ""].Value
            );
        }
        return Task.FromResult(string.Empty);
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            return Task.FromResult(
                localizer[
                    "Progress_LibraryIntegrityCheckTask_Indeterminate",
                    progress.Current
                ].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_LibraryIntegrityCheckTask", progress.Current, progress.Total].Value
        );
    }
}
