using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

public interface ILibraryFilteringService
{
    bool FilterChapter(FoundChapter chapter, IEnumerable<LibraryFilterRule> libraryFilterRules);
}
