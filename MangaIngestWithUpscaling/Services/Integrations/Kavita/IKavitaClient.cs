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
    /// Attempts to update an existing series title in Kavita.
    /// This is preferred over scanning as it maintains the existing series entry.
    /// </summary>
    /// <param name="oldFolderPath">The old folder path where the series was located</param>
    /// <param name="newFolderPath">The new folder path where the series is now located</param>
    /// <param name="newTitle">The new title for the series</param>
    /// <returns>True if the update was successful, false if the series wasn't found or update failed</returns>
    Task<bool> TryUpdateSeries(string oldFolderPath, string newFolderPath, string newTitle);
}
