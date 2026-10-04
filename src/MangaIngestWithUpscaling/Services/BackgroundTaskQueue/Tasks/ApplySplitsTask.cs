using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class ApplySplitsTask : BaseTask, IChapterTask
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
        try
        {
            await splitApplicationService.ApplySplitsAsync(
                ChapterId,
                DetectorVersion,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure; leave the state for the retry/requeue path.
            throw;
        }
        catch
        {
            // A failed apply must not leave the chapter stuck at Processing: RetryFor is 0 and the UI
            // only offers the action from Detected, so the chapter would wedge until the DB is edited.
            // Mirror detection's failure handling.
            var stateManager = services.GetRequiredService<ISplitProcessingStateManager>();
            await stateManager.SetFailedAsync(ChapterId, null, CancellationToken.None);
            throw;
        }
    }
}
