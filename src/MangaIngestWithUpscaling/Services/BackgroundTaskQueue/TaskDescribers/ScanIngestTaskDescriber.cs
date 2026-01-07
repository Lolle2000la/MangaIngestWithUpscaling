using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(ScanIngestTaskDescriber))]
public class ScanIngestTaskDescriber(
    IStringLocalizer<TaskStrings> localizer,
    IDbContextFactory<ApplicationDbContext> dbFactory
) : ITaskDescriber<BaseTask>
{
    public async Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is not ScanIngestTask t)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(t.LibraryName))
            return localizer["Title_ScanIngestTask", t.LibraryName].Value;

        using var db = await dbFactory.CreateDbContextAsync();
        var library = await db.Libraries.FindAsync(t.LibraryId);
        return localizer["Title_ScanIngestTask", library?.Name ?? "Unknown Library"].Value;
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            return Task.FromResult(
                localizer["Progress_ScanIngestTask_Indeterminate", progress.Current].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_ScanIngestTask", progress.Current, progress.Total].Value
        );
    }
}
