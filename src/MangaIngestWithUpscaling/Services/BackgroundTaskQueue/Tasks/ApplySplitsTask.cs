using MangaIngestWithUpscaling.Services.Analysis;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class ApplySplitsTask : BaseTask
{
    public int ChapterId { get; set; }
    public int DetectorVersion { get; set; }

    public override string TaskFriendlyName => $"Applying splits for Chapter {ChapterId}";
    public override int RetryFor { get; set; } = 0;

    public ApplySplitsTask() { }

    public ApplySplitsTask(int chapterId, int detectorVersion)
    {
        ChapterId = chapterId;
        DetectorVersion = detectorVersion;
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
