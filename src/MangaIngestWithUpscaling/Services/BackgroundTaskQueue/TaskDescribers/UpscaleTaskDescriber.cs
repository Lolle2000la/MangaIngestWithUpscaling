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
) : ITaskDescriber<BaseTask>
{
    public async Task<string> GetTitleAsync(BaseTask task)
    {
        if (task is not UpscaleTask t)
        {
            return string.Empty;
        }

        using var db = await dbFactory.CreateDbContextAsync();
        var chapter = await db
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
                    .ThenInclude(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == t.ChapterId);

        if (chapter == null)
        {
            return localizer["Title_UpscaleTask_Unknown", t.ChapterId].Value;
        }

        var profile = await db.UpscalerProfiles.FindAsync(t.UpscalerProfileId);
        var profileName = profile?.Name ?? "Unknown Profile";

        return localizer[
            "Title_UpscaleTask",
            chapter.FileName,
            chapter.Manga.PrimaryTitle,
            profileName
        ].Value;
    }

    public Task<string> GetProgressStatusAsync(BaseTask task, ProgressInfo progress)
    {
        if (progress.IsIndeterminate)
        {
            return Task.FromResult(
                localizer["Progress_UpscaleTask_Indeterminate", progress.Current].Value
            );
        }
        return Task.FromResult(
            localizer["Progress_UpscaleTask", progress.Current, progress.Total].Value
        );
    }
}
