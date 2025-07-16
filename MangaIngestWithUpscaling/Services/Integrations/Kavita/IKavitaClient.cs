namespace MangaIngestWithUpscaling.Services.Integrations.Kavita;

public interface IKavitaClient
{
    /// <summary>
    /// Scans a folder for new manga series and adds them to the library.
    /// The path MUST from the Kavita container, so they might differ from the path known here.
    /// </summary>
    /// <param name="folderPath">The folder to scan for new chapters.</param>
    /// <returns></returns>
    Task ScanFolder(string folderPath);
    
    /// <summary>
    /// Finds a series by its name in Kavita
    /// </summary>
    /// <param name="seriesName">The name of the series to find</param>
    /// <returns>The series information if found, null otherwise</returns>
    Task<KavitaSeriesInfo?> FindSeriesByName(string seriesName);
    
    /// <summary>
    /// Updates the metadata of an existing series in Kavita
    /// </summary>
    /// <param name="seriesId">The ID of the series to update</param>
    /// <param name="newName">The new name for the series</param>
    /// <param name="newSortName">The new sort name for the series</param>
    /// <returns>True if the update was successful, false otherwise</returns>
    Task<bool> UpdateSeriesMetadata(int seriesId, string newName, string? newSortName = null);
    
    /// <summary>
    /// Forces a refresh of series metadata from files
    /// </summary>
    /// <param name="seriesId">The ID of the series to refresh</param>
    /// <returns>True if the refresh was initiated successfully</returns>
    Task<bool> RefreshSeriesMetadata(int seriesId);
}
