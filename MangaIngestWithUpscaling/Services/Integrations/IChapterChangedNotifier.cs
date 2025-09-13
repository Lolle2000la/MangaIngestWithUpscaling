using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.Integrations;

public interface IChapterChangedNotifier
{
    /// <summary>
    /// Notify the integration(s) that a chapter has changed (both additions and new ones).
    /// The scheme may vary depending on the capabilities of the integration (i.e. Kavita may need to scan a folder).
    /// </summary>
    /// <param name="chapter">The chapter to notify.</param>
    /// <param name="upscaled">What variant to notify.</param>
    /// <returns></returns>
    Task Notify(Chapter chapter, bool upscaled);

    /// <summary>
    /// Notify the integration(s) that a manga's title has changed.
    /// This allows integrations to update existing entries rather than creating new ones.
    /// </summary>
    /// <param name="manga">The manga whose title changed.</param>
    /// <param name="oldTitle">The previous title of the manga.</param>
    /// <param name="newTitle">The new title of the manga.</param>
    /// <returns></returns>
    Task NotifyMangaTitleChanged(Manga manga, string oldTitle, string newTitle);
}
