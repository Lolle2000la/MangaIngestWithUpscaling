using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.MangaManagement;

public interface IMangaMerger
{
    /// <summary>
    /// Merges the given manga into the primary manga.
    /// </summary>
    /// <param name="primary">The manga to merge the others into.</param>
    /// <param name="mergedInto">The mangas that will be merged into the primary one. The titles (and other titles) will be merged into the primary manga as other titles.</param>
    /// <param name="cancellationToken">The token to use in to cancel the operation.</param>
    /// <returns></returns>
    Task MergeAsync(Manga primary, IEnumerable<Manga> mergedInto, CancellationToken cancellationToken = default);
}
