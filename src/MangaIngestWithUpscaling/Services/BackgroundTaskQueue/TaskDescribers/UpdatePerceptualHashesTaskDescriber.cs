using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(UpdatePerceptualHashesTaskDescriber))]
public class UpdatePerceptualHashesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<UpdatePerceptualHashesTask>(localizer)
{
    public override Task<string> GetTitleAsync(UpdatePerceptualHashesTask task) =>
        Task.FromResult(Localizer["Title_UpdatePerceptualHashesTask"].Value);
}
