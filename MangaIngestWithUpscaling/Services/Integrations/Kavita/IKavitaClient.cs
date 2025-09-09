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
    /// Finds a series in Kavita by name.
    /// </summary>
    /// <param name="seriesName">The name of the series to find.</param>
    /// <returns>The series information if found, null otherwise.</returns>
    Task<KavitaSeries?> FindSeriesByName(string seriesName);

    /// <summary>
    /// Updates the metadata of an existing series in Kavita.
    /// </summary>
    /// <param name="seriesId">The ID of the series to update.</param>
    /// <param name="newName">The new name for the series.</param>
    /// <returns></returns>
    Task UpdateSeriesMetadata(int seriesId, string newName);

    /// <summary>
    /// Refreshes a series to update its file paths and chapter information.
    /// This should be called after files have been moved to new locations.
    /// </summary>
    /// <param name="seriesId">The ID of the series to refresh.</param>
    /// <returns></returns>
    Task RefreshSeries(int seriesId);
}
