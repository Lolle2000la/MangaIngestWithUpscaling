using MangaIngestWithUpscaling.Data.LibraryManagement;
using System.Runtime.CompilerServices;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition;

public interface IChapterInIngestRecognitionService
{
    public IAsyncEnumerable<FoundChapter> FindAllChaptersAt(string ingestPath,
        IReadOnlyList<LibraryFilterRule>? libraryFilterRules = null,
        CancellationToken cancellationToken = default);
}
