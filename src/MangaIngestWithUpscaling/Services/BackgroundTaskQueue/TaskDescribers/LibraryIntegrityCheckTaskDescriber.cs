using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(LibraryIntegrityCheckTaskDescriber))]
public class LibraryIntegrityCheckTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<LibraryIntegrityCheckTask>(localizer)
{
    public override Task<string> GetTitleAsync(LibraryIntegrityCheckTask task) =>
        Task.FromResult(Localizer["Title_LibraryIntegrityCheckTask", task.LibraryName ?? ""].Value);
}
