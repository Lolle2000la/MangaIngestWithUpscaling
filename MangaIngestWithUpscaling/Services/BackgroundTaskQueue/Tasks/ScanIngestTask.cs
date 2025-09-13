using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.ChapterManagement;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class ScanIngestTask : BaseTask
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;

    public override string TaskFriendlyName => $"Scanning {LibraryName}";

    public override int RetryFor { get; set; } = 3;

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
}