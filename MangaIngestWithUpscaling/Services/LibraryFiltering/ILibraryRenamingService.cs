using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

public interface ILibraryRenamingService
{
    FoundChapter ApplyRenameRules(FoundChapter chapter, IReadOnlyList<LibraryRenameRule> rules);
}
