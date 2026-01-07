using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(LoggingTaskDescriber))]
public class LoggingTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : ITaskDescriber<BaseTask>
{
    public Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is LoggingTask t)
        {
            return Task.FromResult(localizer["Title_LoggingTask", t.Message ?? ""].Value);
        }
        return Task.FromResult(string.Empty);
    }
}
