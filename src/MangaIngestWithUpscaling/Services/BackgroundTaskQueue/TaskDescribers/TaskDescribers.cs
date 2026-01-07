using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped(typeof(LoggingTaskDescriber))]
public class LoggingTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<LoggingTask>(localizer)
{
    public override Task<string> GetTitleAsync(LoggingTask task) =>
        Task.FromResult(Localizer["Title_LoggingTask", task.Message ?? ""].Value);
}

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

[RegisterScoped(typeof(RenameUpscaledChaptersSeriesTaskDescriber))]
public class RenameUpscaledChaptersSeriesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<RenameUpscaledChaptersSeriesTask>(localizer)
{
    public override Task<string> GetTitleAsync(RenameUpscaledChaptersSeriesTask task) =>
        Task.FromResult(
            Localizer[
                "Title_RenameUpscaledChaptersSeriesTask",
                task.ChapterFileName ?? "",
                task.NewTitle ?? ""
            ].Value
        );
}

[RegisterScoped(typeof(LibraryIntegrityCheckTaskDescriber))]
public class LibraryIntegrityCheckTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<LibraryIntegrityCheckTask>(localizer)
{
    public override Task<string> GetTitleAsync(LibraryIntegrityCheckTask task) =>
        Task.FromResult(Localizer["Title_LibraryIntegrityCheckTask", task.LibraryName ?? ""].Value);
}

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

[RegisterScoped(typeof(ApplyImageFiltersTaskDescriber))]
public class ApplyImageFiltersTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<ApplyImageFiltersTask>(localizer)
{
    public override Task<string> GetTitleAsync(ApplyImageFiltersTask task) =>
        Task.FromResult(Localizer["Title_ApplyImageFiltersTask", task.LibraryName ?? ""].Value);
}

[RegisterScoped(typeof(UpdatePerceptualHashesTaskDescriber))]
public class UpdatePerceptualHashesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<UpdatePerceptualHashesTask>(localizer)
{
    public override Task<string> GetTitleAsync(UpdatePerceptualHashesTask task) =>
        Task.FromResult(Localizer["Title_UpdatePerceptualHashesTask"].Value);
}

[RegisterScoped(typeof(DetectSplitCandidatesTaskDescriber))]
public class DetectSplitCandidatesTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<DetectSplitCandidatesTask>(localizer)
{
    public override Task<string> GetTitleAsync(DetectSplitCandidatesTask task) =>
        Task.FromResult(
            !string.IsNullOrEmpty(task.FriendlyEntryName)
                ? task.FriendlyEntryName
                : Localizer["Title_DetectSplitCandidatesTask", task.ChapterId].Value
        );
}

[RegisterScoped(typeof(ApplySplitsTaskDescriber))]
public class ApplySplitsTaskDescriber(IStringLocalizer<TaskStrings> localizer)
    : BaseTaskDescriber<ApplySplitsTask>(localizer)
{
    public override Task<string> GetTitleAsync(ApplySplitsTask task) =>
        Task.FromResult(
            !string.IsNullOrEmpty(task.FriendlyEntryName)
                ? task.FriendlyEntryName
                : Localizer["Title_ApplySplitsTask", task.ChapterId].Value
        );
}
