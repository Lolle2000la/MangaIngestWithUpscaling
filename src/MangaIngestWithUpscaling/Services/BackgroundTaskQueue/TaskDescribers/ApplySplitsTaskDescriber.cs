using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(ApplySplitsTaskDescriber))]
public class ApplySplitsTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<ApplySplitsTask>(localizer)
{
    public override Task<string> GetTitleAsync(ApplySplitsTask task) =>
        Task.FromResult(
            !string.IsNullOrEmpty(task.FriendlyEntryName)
                ? task.FriendlyEntryName
                : Localizer["Title_ApplySplitsTask", task.ChapterId].Value
        );
}
