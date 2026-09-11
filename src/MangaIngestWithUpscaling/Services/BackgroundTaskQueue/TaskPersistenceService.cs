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

        DateTime? processedAt = DateTime.UtcNow;
        // Terminal transitions are guarded: a late or duplicate callback must not overwrite a
        // terminal state. Only tasks that are still Pending/Processing may complete.
        await dbContext
            .PersistedTasks.Where(t =>
                t.Id == taskId
                && (
                    t.Status == PersistedTaskStatus.Pending
                    || t.Status == PersistedTaskStatus.Processing
                )
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(t => t.Status, PersistedTaskStatus.Completed)
                        .SetProperty(t => t.ProcessedAt, processedAt),
                cancellationToken
            );
    }

    public async Task FailTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only a still-active task may fail, and the retry count is incremented in the same
        // conditional update so duplicate failure callbacks cannot inflate it.
        await dbContext
            .PersistedTasks.Where(t =>
                t.Id == taskId
                && (
                    t.Status == PersistedTaskStatus.Pending
                    || t.Status == PersistedTaskStatus.Processing
                )
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(t => t.Status, PersistedTaskStatus.Failed)
                        .SetProperty(t => t.RetryCount, t => t.RetryCount + 1),
                cancellationToken
            );
    }

    public async Task CancelTaskAsync(
        int taskId,
        bool requeue = false,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        PersistedTaskStatus newStatus = requeue
            ? PersistedTaskStatus.Pending
            : PersistedTaskStatus.Canceled;

        // Do not overwrite a terminal state: a late cancel after completion/failure is a no-op.
        await dbContext
            .PersistedTasks.Where(t =>
                t.Id == taskId
                && (
                    t.Status == PersistedTaskStatus.Pending
                    || t.Status == PersistedTaskStatus.Processing
                )
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.Status, newStatus),
                cancellationToken
            );
    }
}
