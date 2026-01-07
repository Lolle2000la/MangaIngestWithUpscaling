using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class ApplySplitsTask : BaseTask
{
    public int ChapterId { get; set; }
    public int DetectorVersion { get; set; }
    public string FriendlyEntryName { get; set; } = string.Empty;

    public override int RetryFor { get; set; } = 0;

    public ApplySplitsTask() { }

    public ApplySplitsTask(int chapterId, int detectorVersion)
    {
        ChapterId = chapterId;
        DetectorVersion = detectorVersion;
    }

    public ApplySplitsTask(Chapter chapter, int detectorVersion)
    {
        ChapterId = chapter.Id;
        DetectorVersion = detectorVersion;
        FriendlyEntryName =
            $"Applying splits for {chapter.FileName} of {chapter.Manga?.PrimaryTitle ?? "Unknown"}";
    }

    public override async Task ProcessAsync(
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        var splitApplicationService = services.GetRequiredService<ISplitApplicationService>();
        await splitApplicationService.ApplySplitsAsync(
            ChapterId,
            DetectorVersion,
            cancellationToken
        );
    }
}
