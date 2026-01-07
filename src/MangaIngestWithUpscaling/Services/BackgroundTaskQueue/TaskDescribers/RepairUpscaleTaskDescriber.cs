using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(RepairUpscaleTaskDescriber))]
public class RepairUpscaleTaskDescriber(
    IStringLocalizer<TaskStrings> localizer,
    IDbContextFactory<ApplicationDbContext> dbFactory
) : BaseTaskDescriber<RepairUpscaleTask>(localizer)
{
    public override async Task<string> GetTitleAsync(RepairUpscaleTask task)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var chapter = await db
            .Chapters.Include(c => c.Manga)
            .FirstOrDefaultAsync(c => c.Id == task.ChapterId);

        if (chapter == null)
        {
            return Localizer["Title_RepairUpscaleTask_Unknown", task.ChapterId].Value;
        }

        var profile = await db.UpscalerProfiles.FindAsync(task.UpscalerProfileId);
        var profileName = profile?.Name ?? "Unknown Profile";

        return Localizer[
            "Title_RepairUpscaleTask",
            chapter.FileName,
            chapter.Manga.PrimaryTitle,
            profileName
        ].Value;
    }
}
