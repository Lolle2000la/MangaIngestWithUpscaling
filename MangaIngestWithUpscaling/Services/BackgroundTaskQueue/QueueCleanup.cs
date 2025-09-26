using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

[RegisterScoped]
public class QueueCleanup(ILogger<QueueCleanup> _logger) : IQueueCleanup
{
    public async Task CleanupAsync(ApplicationDbContext context)
    {
        // Existing cleanup code...
        var oldTasks = await context
            .PersistedTasks.Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .ToListAsync();

        if (oldTasks.Count > 25)
            _logger.LogInformation("Cleaning up {TaskCount} old tasks.", oldTasks.Count);

        context.PersistedTasks.RemoveRange(oldTasks);
        await context.SaveChangesAsync();
    }
}
