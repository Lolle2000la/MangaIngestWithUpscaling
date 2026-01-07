using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(DetectSplitCandidatesTaskDescriber))]
public class DetectSplitCandidatesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<DetectSplitCandidatesTask>(localizer)
{
    public override Task<string> GetTitleAsync(DetectSplitCandidatesTask task) =>
        Task.FromResult(
            !string.IsNullOrEmpty(task.FriendlyEntryName)
                ? task.FriendlyEntryName
                : Localizer["Title_DetectSplitCandidatesTask", task.ChapterId].Value
        );
}
