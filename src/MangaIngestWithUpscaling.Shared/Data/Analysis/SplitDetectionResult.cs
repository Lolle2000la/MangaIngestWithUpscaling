using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Data.Analysis;

public class SplitDetectionResult
{
    [JsonPropertyName("image")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("original_height")]
    public int OriginalHeight { get; set; }

    [JsonPropertyName("original_width")]
    public int OriginalWidth { get; set; }

    [JsonPropertyName("splits")]
    public List<DetectedSplit> Splits { get; set; } = [];

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class DetectedSplit
{
    [JsonPropertyName("y_original")]
    public int YOriginal { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

/// <summary>
/// Helper methods for working with split detection results
/// </summary>
public static class SplitDetectionResultHelper
{
    /// <summary>
    /// Merges two split detection results, combining their splits while removing duplicates
    /// </summary>
    /// <param name="existing">The existing detection result</param>
    /// <param name="newResult">The new detection result to merge</param>
    /// <returns>A new detection result containing all unique splits from both inputs</returns>
    public static SplitDetectionResult Merge(SplitDetectionResult existing, SplitDetectionResult newResult)
    {
        if (existing == null)
            return newResult;
        
        if (newResult == null)
            return existing;

        // Merge splits: combine existing and new splits, removing duplicates
        var allSplits = existing.Splits.Concat(newResult.Splits)
            .GroupBy(s => s.YOriginal) // Group by position to remove duplicates
            .Select(g => g.First())    // Take first of each group
            .OrderBy(s => s.YOriginal) // Order by position
            .ToList();

        // Create merged result with all splits preserved
        return new SplitDetectionResult
        {
            ImagePath = newResult.ImagePath ?? existing.ImagePath,
            OriginalHeight = newResult.OriginalHeight != 0 ? newResult.OriginalHeight : existing.OriginalHeight,
            OriginalWidth = newResult.OriginalWidth != 0 ? newResult.OriginalWidth : existing.OriginalWidth,
            Splits = allSplits,
            Count = allSplits.Count,
            Error = newResult.Error ?? existing.Error
        };
    }

    /// <summary>
    /// Creates a copy of a SplitDetectionResult with updated splits
    /// </summary>
    /// <param name="original">The original detection result</param>
    /// <param name="newSplits">The new splits to use</param>
    /// <returns>A new detection result with the updated splits</returns>
    public static SplitDetectionResult WithSplits(SplitDetectionResult original, List<DetectedSplit> newSplits)
    {
        if (original == null)
            throw new ArgumentNullException(nameof(original));
        
        if (newSplits == null)
            throw new ArgumentNullException(nameof(newSplits));

        return new SplitDetectionResult
        {
            ImagePath = original.ImagePath,
            OriginalHeight = original.OriginalHeight,
            OriginalWidth = original.OriginalWidth,
            Splits = newSplits,
            Count = newSplits.Count,
            Error = original.Error
        };
    }
}
