using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(UpscaleTaskDescriber))]
public class UpscaleTaskDescriber(
    IStringLocalizer<TaskStrings> localizer,
    IDbContextFactory<ApplicationDbContext> dbFactory
) : BaseTaskDescriber<UpscaleTask>(localizer)
{
    public override async Task<string> GetTitleAsync(UpscaleTask task)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var chapter = await db
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
                    .ThenInclude(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == task.ChapterId);

        if (chapter == null)
        {
            return Localizer["Title_UpscaleTask_Unknown", task.ChapterId].Value;
        }

        var profile = await db.UpscalerProfiles.FindAsync(task.UpscalerProfileId);
        var profileName = profile?.Name ?? "Unknown Profile";

        return Localizer[
            "Title_UpscaleTask",
            chapter.FileName,
            chapter.Manga.PrimaryTitle,
            profileName
        ].Value;
    }
}
