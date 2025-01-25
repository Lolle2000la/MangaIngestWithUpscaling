using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering
{
    public interface ILibraryFilteringService
    {
        List<FoundChapter> FilterChapters(List<FoundChapter> chapters, IEnumerable<LibraryFilterRule> libraryFilterRules);
    }
}
