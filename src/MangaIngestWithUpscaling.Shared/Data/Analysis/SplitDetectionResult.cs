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
