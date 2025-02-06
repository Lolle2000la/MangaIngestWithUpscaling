
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public class QueueCleanup(
    ApplicationDbContext dbContext,
    ILogger<QueueCleanup> _logger) : IQueueCleanup
{
    public async Task CleanupAsync()
    {
        // Existing cleanup code...
        var oldTasks = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .ToListAsync();

        if (oldTasks.Count > 25)
            _logger.LogInformation("Cleaning up {TaskCount} old tasks.", oldTasks.Count);

        dbContext.PersistedTasks.RemoveRange(oldTasks);
        await dbContext.SaveChangesAsync();
    }
}
