using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

[RegisterScoped]
public class QueueCleanup(
    ApplicationDbContext dbContext,
    ITaskQueue taskQueue,
    ILogger<QueueCleanup> _logger
) : IQueueCleanup
{
    public async Task CleanupAsync()
    {
        // Existing cleanup code...
        var oldTasks = await dbContext
            .PersistedTasks.Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .ToListAsync();

        if (oldTasks.Count == 0)
            return;

        if (oldTasks.Count > 25)
            _logger.LogInformation("Cleaning up {TaskCount} old tasks.", oldTasks.Count);

        // Use TaskQueue.RemoveTasksAsync to ensure TaskRegistry is notified
        await taskQueue.RemoveTasksAsync(oldTasks);
    }
}
