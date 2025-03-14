using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.LibraryIntegrety;

public class LibraryIntegrityChecker(
    ApplicationDbContext dbContext) : ILibraryIntegrityChecker
{
    public async Task CheckIntegrity(CancellationToken cancellationToken)
    {
        var libraries = await dbContext.Libraries
            .Include(l => l.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.Chapters)
            .ThenInclude(c => c.UpscalerProfile)
                .Include(l => l.MangaSeries)
                    .ThenInclude(m => m.OtherTitles)
            .ToListAsync(cancellationToken);

        foreach (var library in libraries)
        {
            await CheckIntegrity(library, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Library library, CancellationToken cancellationToken)
    {
        foreach (var manga in library.MangaSeries)
        {
            await CheckIntegrity(manga, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Manga manga, CancellationToken cancellationToken)
    {
        foreach (var chapter in manga.Chapters)
        {
            await CheckIntegrity(chapter, cancellationToken);
        }
    }

    public async Task CheckIntegrity(Chapter chapter, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
