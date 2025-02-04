using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public interface IMangaMetadataChanger
{
    Task ChangeTitle(Manga manga, string newTitle, bool addOldToAlternative = true);
}
