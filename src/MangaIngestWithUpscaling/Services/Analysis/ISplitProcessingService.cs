using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Services.Analysis;

public interface ISplitProcessingService
{
    Task ProcessDetectionResultAsync(
        int chapterId,
        SplitDetectionResult result,
        int detectorVersion,
        CancellationToken cancellationToken,
        ApplicationDbContext dbContext
    );
    Task ProcessDetectionResultsAsync(
        int chapterId,
        IEnumerable<SplitDetectionResult> results,
        int detectorVersion,
        CancellationToken cancellationToken,
        ApplicationDbContext dbContext
    );
    Task QueueSplitDetectionAsync(int chapterId, ApplicationDbContext dbContext);
    Task<List<StripSplitFinding>> GetSplitFindingsAsync(
        int chapterId,
        ApplicationDbContext dbContext
    );
}
