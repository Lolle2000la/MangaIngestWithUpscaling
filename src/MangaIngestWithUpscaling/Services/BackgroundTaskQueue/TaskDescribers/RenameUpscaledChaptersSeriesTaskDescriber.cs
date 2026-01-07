using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(RenameUpscaledChaptersSeriesTaskDescriber))]
public class RenameUpscaledChaptersSeriesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is RenameUpscaledChaptersSeriesTask t)
        {
            return Task.FromResult(
                localizer[
                    "Title_RenameUpscaledChaptersSeriesTask",
                    t.ChapterFileName ?? "",
                    t.NewTitle ?? ""
                ].Value
            );
        }
        return Task.FromResult(string.Empty);
    }
}
