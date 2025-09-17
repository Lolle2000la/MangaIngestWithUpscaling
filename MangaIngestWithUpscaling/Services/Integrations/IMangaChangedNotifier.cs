using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.Integrations;

/// <summary>
/// Interface for notifying external services about manga-level changes (e.g., title changes)
/// </summary>
public interface IMangaChangedNotifier
{
    /// <summary>
    /// Notifies external services about a manga title change
    /// </summary>
    /// <param name="manga">The manga that was changed</param>
    /// <param name="oldTitle">The old title of the manga</param>
    /// <param name="oldFolderPath">The old folder path where the manga was located</param>
    /// <param name="newFolderPath">The new folder path where the manga is now located</param>
    /// <returns>A task representing the async operation</returns>
    Task NotifyTitleChanged(Manga manga, string oldTitle, string oldFolderPath, string newFolderPath);
}