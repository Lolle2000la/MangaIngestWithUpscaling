using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface ITaskPersistenceService
{
    Task<bool> ClaimTaskAsync(int taskId, CancellationToken cancellationToken = default);
    Task CompleteTaskAsync(int taskId, CancellationToken cancellationToken = default);
    Task FailTaskAsync(int taskId, CancellationToken cancellationToken = default);
    Task CancelTaskAsync(
        int taskId,
        bool requeue = false,
        CancellationToken cancellationToken = default
    );
}

[RegisterSingleton]
public class TaskPersistenceService(IServiceScopeFactory scopeFactory) : ITaskPersistenceService
{
    public async Task<bool> ClaimTaskAsync(
        int taskId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            cancellationToken
        );
        if (task == null || task.Status != PersistedTaskStatus.Pending)
        {
            return false;
        }

        task.Status = PersistedTaskStatus.Processing;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task CompleteTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            cancellationToken
        );
        if (task != null)
        {
            task.Status = PersistedTaskStatus.Completed;
            task.ProcessedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task FailTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            cancellationToken
        );
        if (task != null)
        {
            task.Status = PersistedTaskStatus.Failed;
            task.RetryCount++;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task CancelTaskAsync(
        int taskId,
        bool requeue = false,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            cancellationToken
        );
        if (task != null)
        {
            task.Status = requeue ? PersistedTaskStatus.Pending : PersistedTaskStatus.Canceled;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
