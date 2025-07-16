using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.Integrations;

/// <summary>
/// Interface for notifying integrations about manga title changes
/// </summary>
public interface ITitleChangedNotifier
{
    /// <summary>
    /// Notify the integration(s) that a manga title has changed.
    /// This allows integrations to update existing entities instead of creating new ones.
    /// </summary>
    /// <param name="oldTitle">The previous title of the manga</param>
    /// <param name="newTitle">The new title of the manga</param>
    /// <param name="sampleChapter">A representative chapter from the manga to determine library context</param>
    /// <returns>True if the notification was processed successfully</returns>
    Task<bool> NotifyTitleChanged(string oldTitle, string newTitle, Chapter sampleChapter);
}
