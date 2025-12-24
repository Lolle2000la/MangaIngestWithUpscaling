using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using NetVips;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

public interface ISplitApplier
{
    List<string> ApplySplitsToImage(string imagePath, List<DetectedSplit> splits, string outputDir);
}

[RegisterScoped]
public class SplitApplier : ISplitApplier
{
    public List<string> ApplySplitsToImage(
        string imagePath,
        List<DetectedSplit> splits,
        string outputDir
    )
    {
        var resultPaths = new List<string>();
        using var image = Image.NewFromFile(imagePath);

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        var ext = Path.GetExtension(imagePath);

        int currentY = 0;
        int partIndex = 1;

        // Sort splits by Y just in case
        var sortedSplits = splits.OrderBy(s => s.YOriginal).ToList();

        foreach (var split in sortedSplits)
        {
            int splitY = split.YOriginal;

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
}
