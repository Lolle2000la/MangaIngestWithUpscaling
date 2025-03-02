using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public interface IMangaMetadataChanger
{
    Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = true);
    void ApplyUpscaledChapterTitle(Chapter chapter, string newTitle, string origChapterPath);
}
