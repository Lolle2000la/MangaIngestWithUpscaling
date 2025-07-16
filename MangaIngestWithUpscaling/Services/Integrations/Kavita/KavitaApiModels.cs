namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

/// <summary>
/// Represents series information returned from Kavita API
/// </summary>
public record KavitaSeriesInfo
{
    public required int Id { get; set; }
    public required string Name { get; set; }
    public required string LocalizedName { get; set; }
    public required string SortName { get; set; }
    public required int LibraryId { get; set; }
    public required string LibraryName { get; set; }
}

/// <summary>
/// Request to update series metadata in Kavita
/// </summary>
public record UpdateSeriesMetadataRequest
{
    public required string ApiKey { get; set; }
    public required int Id { get; set; }
    public required string Name { get; set; }
    public required string LocalizedName { get; set; }
    public required string SortName { get; set; }
    public required bool SortNameLocked { get; set; }
    public required bool LocalizedNameLocked { get; set; }
    public required bool CoverImageLocked { get; set; }
}

/// <summary>
/// Request to refresh series metadata in Kavita
/// </summary>
public record RefreshSeriesMetadataRequest
{
    public required string ApiKey { get; set; }
    public required int LibraryId { get; set; }
    public required int SeriesId { get; set; }
    public required bool ForceUpdate { get; set; }
    public required bool ForceColorscape { get; set; }
}

/// <summary>
/// Response from Kavita API containing series data
/// </summary>
public record KavitaSeriesResponse
{
    public required int Id { get; set; }
    public required string Name { get; set; }
    public required string LocalizedName { get; set; }
    public required string SortName { get; set; }
    public required int LibraryId { get; set; }
    public required KavitaLibraryInfo Library { get; set; }
}

/// <summary>
/// Library information from Kavita
/// </summary>
public record KavitaLibraryInfo
{
    public required int Id { get; set; }
    public required string Name { get; set; }
}
