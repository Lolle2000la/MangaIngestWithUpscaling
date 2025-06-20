using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using System.Runtime.CompilerServices;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition;

/// <summary>
/// Provides services for recognizing chapters in the ingest path.
/// Not everything in the ingest path is a chapter, so this service
/// identifies what is and what isn't.
/// </summary>
public interface IChapterInIngestRecognitionService
{
    /// <summary>
    /// Finds all chapters in the ingest path, processing files concurrently on background threads.
    /// </summary>
    /// <param name="ingestPath">The path to search for chapters.</param>
    /// <param name="libraryFilterRules">Optional rules to filter the found chapters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of found chapters.</returns>
    public IAsyncEnumerable<FoundChapter> FindAllChaptersAt(string ingestPath,
        IReadOnlyList<LibraryFilterRule>? libraryFilterRules = null,
        CancellationToken cancellationToken = default);
}
