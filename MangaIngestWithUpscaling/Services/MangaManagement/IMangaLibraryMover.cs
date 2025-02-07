using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

public interface IMangaLibraryMover
{
    /// <summary>
    /// Moves a manga from one library to another.
    /// </summary>
    /// <param name="manga">The manga to move.</param>
    /// <param name="targetLibrary">The library to move the manga to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    Task MoveMangaAsync(Manga manga, Library targetLibrary, CancellationToken cancellationToken = default);
}
