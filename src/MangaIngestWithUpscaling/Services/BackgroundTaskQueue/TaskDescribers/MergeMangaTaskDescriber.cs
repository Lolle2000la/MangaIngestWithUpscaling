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
) : BaseTaskDescriber<MergeMangaTask>(localizer)
{
    public override async Task<string> GetTitleAsync(MergeMangaTask task)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var manga = await db.MangaSeries.FindAsync(task.IntoMangaId);
        return Localizer[
            "Title_MergeMangaTask",
            task.ToMerge?.Count ?? 0,
            manga?.PrimaryTitle ?? "Unknown Manga"
        ].Value;
    }
}
