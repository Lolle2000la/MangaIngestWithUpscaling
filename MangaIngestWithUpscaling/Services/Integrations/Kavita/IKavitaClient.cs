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
}
