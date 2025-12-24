using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Services.Analysis;

public interface ISplitProcessingService
{
    Task ProcessDetectionResultAsync(
        int chapterId,
        SplitDetectionResult result,
        int detectorVersion,
        CancellationToken cancellationToken
    );
    Task ProcessDetectionResultsAsync(
        int chapterId,
        IEnumerable<SplitDetectionResult> results,
        int detectorVersion,
        CancellationToken cancellationToken
    );
    Task QueueSplitDetectionAsync(int chapterId);
    Task<List<StripSplitFinding>> GetSplitFindingsAsync(int chapterId);
}
