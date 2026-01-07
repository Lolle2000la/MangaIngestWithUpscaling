using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(MergeMangaTaskDescriber))]
public class MergeMangaTaskDescriber(
    IStringLocalizer<TaskStrings> localizer,
    IDbContextFactory<ApplicationDbContext> dbFactory
) : ITaskDescriber<BaseTask>
{
    public async Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is MergeMangaTask t)
        {
            using var db = await dbFactory.CreateDbContextAsync();
            var manga = await db.MangaSeries.FindAsync(t.IntoMangaId);
            return localizer[
                "Title_MergeMangaTask",
                t.ToMerge?.Count ?? 0,
                manga?.PrimaryTitle ?? "Unknown Manga"
            ].Value;
        }
        return string.Empty;
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            return Task.FromResult(
                localizer["Progress_MergeMangaTask_Indeterminate", progress.Current].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_MergeMangaTask", progress.Current, progress.Total].Value
        );
    }
}
