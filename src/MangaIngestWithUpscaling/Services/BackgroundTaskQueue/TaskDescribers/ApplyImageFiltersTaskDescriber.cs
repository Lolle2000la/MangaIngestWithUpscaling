using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(ApplyImageFiltersTaskDescriber))]
public class ApplyImageFiltersTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is ApplyImageFiltersTask t)
        {
            return Task.FromResult(
                localizer["Title_ApplyImageFiltersTask", t.LibraryName ?? ""].Value
            );
        }
        return Task.FromResult(string.Empty);
    }
}
