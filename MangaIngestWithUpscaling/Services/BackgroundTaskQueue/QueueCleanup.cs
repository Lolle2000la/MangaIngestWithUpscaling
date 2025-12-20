using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

[RegisterScoped]
public class QueueCleanup(ApplicationDbContext dbContext, ILogger<QueueCleanup> _logger)
    : IQueueCleanup
{
    public async Task CleanupAsync()
    {
        // In QueueCleanup.cs
        var cutoffDate = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .Select(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (cutoffDate != default)
        {
            var deletedCount = await dbContext.PersistedTasks
                .Where(t => t.Status == PersistedTaskStatus.Completed && t.CreatedAt <= cutoffDate)
                .ExecuteDeleteAsync();
        
            if (deletedCount > 0)
                _logger.LogInformation("Cleaned up {Count} old tasks.", deletedCount);
        }
    }
}
