using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.CbzConversion;
using MangaIngestWithUpscaling.Services.ChapterManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class ScanIngestTask : BaseTask
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<ScanIngestTask>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var library = await dbContext.Libraries.FindAsync([LibraryId], cancellationToken: cancellationToken);
        var ingestProcessor = services.GetRequiredService<IIngestProcessor>();

        if (library == null)
        {
            throw new InvalidOperationException($"Library with ID {LibraryId} not found.");
        }
        else
        {
            LibraryName = library.Name;
            await ingestProcessor.ProcessAsync(library, cancellationToken);
        }
    }

    public override string TaskFriendlyName => $"Scanning {LibraryName}";
}
