
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class LibraryIntegrityCheckTask : BaseTask
{
    public override string TaskFriendlyName => $"Checking integrity for {LibraryName}";

    public string LibraryName { get; set; } = string.Empty;

    public int LibraryId { get; set; }

    public LibraryIntegrityCheckTask() { }

    public LibraryIntegrityCheckTask(Library library)
    {
        LibraryName = library.Name;
        LibraryId = library.Id;
    }

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var integrityChecker = services.GetRequiredService<ILibraryIntegrityChecker>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        var library = await dbContext.Libraries
            .Include(l => l.MangaSeries)
                .ThenInclude(m => m.Chapters)
                    .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(l => l.Id == LibraryId, cancellationToken);

        if (library == null)
        {
            throw new InvalidOperationException($"Library with ID {LibraryId} not found.");
        }

        await integrityChecker.CheckIntegrity(library, cancellationToken);
    }
}
