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
) : BaseTaskDescriber<ScanIngestTask>(localizer)
{
    public override async Task<string> GetTitleAsync(ScanIngestTask task)
    {
        if (!string.IsNullOrEmpty(task.LibraryName))
            return Localizer["Title_ScanIngestTask", task.LibraryName].Value;

        using var db = await dbFactory.CreateDbContextAsync();
        var library = await db.Libraries.FindAsync(task.LibraryId);
        return Localizer["Title_ScanIngestTask", library?.Name ?? "Unknown Library"].Value;
    }
}
