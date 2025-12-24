namespace MangaIngestWithUpscaling.Services.Analysis;

public interface ISplitApplicationService
{
    Task ApplySplitsAsync(int chapterId, int detectorVersion, CancellationToken cancellationToken);
}
