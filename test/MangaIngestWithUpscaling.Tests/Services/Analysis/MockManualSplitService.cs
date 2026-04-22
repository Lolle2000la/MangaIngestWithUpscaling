using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class MockManualSplitService : IManualSplitService
{
    public List<string> ApplyManualSplitsToImage(
        string imagePath,
        List<int> splitPositions,
        string outputDir
    )
    {
        // Simulate splitting an image at the specified positions
        // Return dummy output file paths
        var outputFiles = new List<string>();

        for (int i = 0; i < splitPositions.Count + 1; i++)
        {
            string outputFile = Path.Combine(
                outputDir,
                $"{Path.GetFileNameWithoutExtension(imagePath)}_part{i}{Path.GetExtension(imagePath)}"
            );
            outputFiles.Add(outputFile);
        }

        return outputFiles;
    }

    public List<string> ApplyEqualSplitsToImage(
        string imagePath,
        int numberOfSplits,
        string outputDir
    )
    {
        // Simulate splitting an image into equal parts
        // Return dummy output file paths
        var outputFiles = new List<string>();

        for (int i = 0; i < numberOfSplits; i++)
        {
            string outputFile = Path.Combine(
                outputDir,
                $"{Path.GetFileNameWithoutExtension(imagePath)}_equal{i}{Path.GetExtension(imagePath)}"
            );
            outputFiles.Add(outputFile);
        }

        return outputFiles;
    }

    public int ManualSplitVersion => 2;
}
