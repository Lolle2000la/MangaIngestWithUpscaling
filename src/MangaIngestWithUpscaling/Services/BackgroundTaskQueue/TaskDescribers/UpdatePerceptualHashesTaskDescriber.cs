using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(UpdatePerceptualHashesTaskDescriber))]
public class UpdatePerceptualHashesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is UpdatePerceptualHashesTask)
        {
            return Task.FromResult(localizer["Title_UpdatePerceptualHashesTask"].Value);
        }
        return Task.FromResult(string.Empty);
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            return Task.FromResult(
                localizer[
                    "Progress_UpdatePerceptualHashesTask_Indeterminate",
                    progress.Current
                ].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_UpdatePerceptualHashesTask", progress.Current, progress.Total].Value
        );
    }
}
