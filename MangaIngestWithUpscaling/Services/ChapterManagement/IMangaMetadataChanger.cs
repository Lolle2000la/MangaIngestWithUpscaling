using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

public interface IMangaMetadataChanger
{
    Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = false);
}
