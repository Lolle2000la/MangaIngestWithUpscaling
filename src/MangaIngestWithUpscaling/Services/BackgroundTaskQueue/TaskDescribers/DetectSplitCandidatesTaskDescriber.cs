using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(DetectSplitCandidatesTaskDescriber))]
public class DetectSplitCandidatesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is DetectSplitCandidatesTask t)
        {
            return Task.FromResult(
                !string.IsNullOrEmpty(t.FriendlyEntryName)
                    ? t.FriendlyEntryName
                    : localizer["Title_DetectSplitCandidatesTask", t.ChapterId].Value
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
                    "Progress_DetectSplitCandidatesTask_Indeterminate",
                    progress.Current
                ].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_DetectSplitCandidatesTask", progress.Current, progress.Total].Value
        );
    }
}
