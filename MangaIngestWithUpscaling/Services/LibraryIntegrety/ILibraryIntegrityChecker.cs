using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrety;

public interface ILibraryIntegrityChecker
{
    Task CheckIntegrity(Library library, CancellationToken cancellationToken);
    Task CheckIntegrity(Manga manga, CancellationToken cancellationToken);
    Task CheckIntegrity(Chapter chapter, CancellationToken cancellationToken);
    Task CheckIntegrity(CancellationToken cancellationToken);
}
