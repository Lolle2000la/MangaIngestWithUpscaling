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
}
