using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterRecognition
{
    public interface IChapterInIngestRecognitionService
    {
        List<FoundChapter> FindAllChaptersAt(string ingestPath, IReadOnlyList<LibraryFilterRule> libraryFilterRules = null);
    }
}
