using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.LibraryIntegrity;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class LibraryIntegrityCheckTask : BaseTask
{
    public LibraryIntegrityCheckTask() { }

    public LibraryIntegrityCheckTask(Library library)
    {
        LibraryName = library.Name;
        LibraryId = library.Id;
    }

    public override string TaskFriendlyName => $"Checking integrity for {LibraryName}";

    public string LibraryName { get; set; } = string.Empty;

    public int LibraryId { get; set; }

    public override async Task ProcessAsync(
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        var integrityChecker = services.GetRequiredService<ILibraryIntegrityChecker>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        var library = await dbContext
            .Libraries.Include(l => l.MangaSeries)
                .ThenInclude(m => m.Chapters)
                    .ThenInclude(c => c.UpscalerProfile)
            .Include(l => l.UpscalerProfile)
            .FirstOrDefaultAsync(l => l.Id == LibraryId, cancellationToken);

        if (library == null)
        {
            throw new InvalidOperationException($"Library with ID {LibraryId} not found.");
        }

        // Prepare progress reporter
        Progress.ProgressUnit = "chapters";
        var reporter = new Progress<IntegrityProgress>(p =>
        {
            if (p.Total.HasValue)
            {
                Progress.Total = p.Total.Value;
            }

            if (p.Current.HasValue)
            {
                Progress.Current = p.Current.Value;
            }

            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
            {
                Progress.StatusMessage = p.StatusMessage!;
            }
        });

        await integrityChecker.CheckIntegrity(library, reporter, cancellationToken);
    }
}
