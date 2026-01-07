using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(LoggingTaskDescriber))]
public class LoggingTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<LoggingTask>(localizer)
{
    public override Task<string> GetTitleAsync(LoggingTask task) =>
        Task.FromResult(Localizer["Title_LoggingTask", task.Message ?? ""].Value);
}
