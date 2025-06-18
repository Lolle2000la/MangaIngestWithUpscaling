namespace MangaIngestWithUpscaling.Data.LogModel;

public class Log
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public string RenderedMessage { get; set; } = string.Empty;
    public string? Properties { get; set; }
}