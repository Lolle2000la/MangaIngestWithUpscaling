using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

public interface IManualSplitService
{
    List<string> ApplyManualSplitsToImage(
        string imagePath,
        List<int> splitPositions,
        string outputDir
    );
    List<string> ApplyEqualSplitsToImage(string imagePath, int numberOfSplits, string outputDir);

    /// <summary>
    /// Version stamped on manual split findings. It is a separate sequence from the auto-detector's:
    /// a manual split is drawn by the user, not produced by detection, so no re-detection is owed.
    /// </summary>
    int ManualSplitVersion { get; }
}

[RegisterScoped]
public class ManualSplitService : IManualSplitService
{
    // Manual splits are not a detector output, so they carry their own version. The number happens to
    // equal the current auto-detector version (SplitDetectionService.CURRENT_DETECTOR_VERSION == 2);
    // it is a separate sequence, not a claim that the two are interchangeable.
    public int ManualSplitVersion => 2;

    public List<string> ApplyManualSplitsToImage(
        string imagePath,
        List<int> splitPositions,
        string outputDir
    )
    {
        var resultPaths = new List<string>();
        using var image = Image.NewFromFile(imagePath);

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        var ext = Path.GetExtension(imagePath);

        // Sort split positions
        var sortedSplits = splitPositions.OrderBy(y => y).ToList();

        int currentY = 0;
        int partIndex = 1;

        foreach (var splitY in sortedSplits)
        {
            // Validate splitY
            if (splitY <= currentY || splitY >= image.Height)
                continue;

            int height = splitY - currentY;
            using var crop = image.Crop(0, currentY, image.Width, height);

            var partName = $"{fileNameWithoutExt}_part{partIndex}{ext}";
            var partPath = Path.Combine(outputDir, partName);
            crop.WriteToFile(partPath);
            resultPaths.Add(partPath);

            currentY = splitY;
            partIndex++;
        }

        // Last part
        if (currentY < image.Height)
        {
            int height = image.Height - currentY;
            using var crop = image.Crop(0, currentY, image.Width, height);

            var partName = $"{fileNameWithoutExt}_part{partIndex}{ext}";
            var partPath = Path.Combine(outputDir, partName);
            crop.WriteToFile(partPath);
            resultPaths.Add(partPath);
        }

        return resultPaths;
    }

    public List<string> ApplyEqualSplitsToImage(
        string imagePath,
        int numberOfSplits,
        string outputDir
    )
    {
        if (numberOfSplits <= 0)
            throw new ArgumentException("Number of splits must be greater than 0");

        var resultPaths = new List<string>();
        using var image = Image.NewFromFile(imagePath);

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        var ext = Path.GetExtension(imagePath);

        int partHeight = image.Height / numberOfSplits;
        int currentY = 0;

        for (int i = 0; i < numberOfSplits; i++)
        {
            // For the last part, take the remaining height to avoid rounding errors
            int height = (i == numberOfSplits - 1) ? (image.Height - currentY) : partHeight;

            using var crop = image.Crop(0, currentY, image.Width, height);

            var partName = $"{fileNameWithoutExt}_part{i + 1}{ext}";
            var partPath = Path.Combine(outputDir, partName);
            crop.WriteToFile(partPath);
            resultPaths.Add(partPath);

            currentY += height;
        }

        return resultPaths;
    }
}
