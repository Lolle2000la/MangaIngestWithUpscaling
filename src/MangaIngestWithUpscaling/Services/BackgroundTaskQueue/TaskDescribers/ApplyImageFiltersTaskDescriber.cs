using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(ApplyImageFiltersTaskDescriber))]
public class ApplyImageFiltersTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<ApplyImageFiltersTask>(localizer)
{
    public override Task<string> GetTitleAsync(ApplyImageFiltersTask task) =>
        Task.FromResult(Localizer["Title_ApplyImageFiltersTask", task.LibraryName ?? ""].Value);
}
