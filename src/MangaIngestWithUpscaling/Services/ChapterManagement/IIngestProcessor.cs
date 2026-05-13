using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.Services.ChapterManagement;

public interface IIngestProcessor
{
    Task ProcessAsync(
        Library library,
        CancellationToken cancellationToken,
        ApplicationDbContext dbContext
    );
}
