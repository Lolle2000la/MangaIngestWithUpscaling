using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class MockSplitDetectionService : ISplitDetectionService
{
    public Task<List<SplitDetectionResult>> DetectSplitsAsync(
        string inputPath,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Simulate some work
        if (Directory.Exists(inputPath))
        {
            var files = Directory.GetFiles(inputPath, "*.jpg");
            var results = new List<SplitDetectionResult>();

            foreach (var file in files)
            {
                results.Add(
                    new SplitDetectionResult
                    {
                        ImagePath = file,
                        Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                    }
                );
            }

            if (results.Count > 0)
                return Task.FromResult(results);
        }

        // Fallback dummy result if no files found or input is file
        var result = new SplitDetectionResult
        {
            ImagePath = inputPath.EndsWith(".jpg") ? inputPath : Path.Combine(inputPath, "001.jpg"),
            Splits =
            [
                new DetectedSplit { YOriginal = 100, Confidence = 0.9 },
                new DetectedSplit { YOriginal = 200, Confidence = 0.9 },
            ],
        };

        return Task.FromResult(new List<SplitDetectionResult> { result });
    }
}
