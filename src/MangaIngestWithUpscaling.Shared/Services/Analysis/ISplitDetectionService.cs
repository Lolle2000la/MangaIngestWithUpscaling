using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

public interface ISplitDetectionService
{
    Task<List<SplitDetectionResult>> DetectSplitsAsync(
        string inputPath,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}
