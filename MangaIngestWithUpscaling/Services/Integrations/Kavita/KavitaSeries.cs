namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

/// <summary>
/// Represents a series in Kavita with the essential information needed for updates.
/// </summary>
public class KavitaSeries
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LocalizedName { get; set; }
    public string? OriginalName { get; set; }
    public int LibraryId { get; set; }
}

/// <summary>
/// Request model for updating series metadata.
/// </summary>
public class UpdateSeriesRequest
{
    public required string ApiKey { get; set; }
    public required string Name { get; set; }
    public string? LocalizedName { get; set; }
    public string? OriginalName { get; set; }
}

/// <summary>
/// Request model for refreshing a series.
/// </summary>
public class RefreshSeriesRequest
{
    public required string ApiKey { get; set; }
}