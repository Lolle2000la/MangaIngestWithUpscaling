using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(RenameUpscaledChaptersSeriesTaskDescriber))]
public class RenameUpscaledChaptersSeriesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<RenameUpscaledChaptersSeriesTask>(localizer)
{
    public override Task<string> GetTitleAsync(RenameUpscaledChaptersSeriesTask task) =>
        Task.FromResult(
            Localizer[
                "Title_RenameUpscaledChaptersSeriesTask",
                task.ChapterFileName ?? "",
                task.NewTitle ?? ""
            ].Value
        );
}
