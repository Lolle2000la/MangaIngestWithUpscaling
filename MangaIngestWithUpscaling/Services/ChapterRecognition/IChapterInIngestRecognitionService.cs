using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition;

public interface IChapterInIngestRecognitionService
{
    List<FoundChapter> FindAllChaptersAt(string ingestPath, IReadOnlyList<LibraryFilterRule>? libraryFilterRules = null);
}
