using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface ITaskPersistenceService
{
    Task<bool> ClaimTaskAsync(int taskId, CancellationToken cancellationToken = default);
    Task<int> CompleteTaskAsync(int taskId, CancellationToken cancellationToken = default);
    Task<int> FailTaskAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels a still-active task (Pending or Processing). The affected row count is returned so
    ///     callers can avoid mirroring a terminal state that the guarded write did not apply.
    /// </summary>
    Task<int> CancelTaskAsync(
        int taskId,
        bool requeue = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Puts a still-recoverable task row (Pending or Processing) back to
    ///     <see cref="PersistedTaskStatus.Pending"/>. Terminal rows are left untouched so a late
    ///     recovery cannot resurrect a completed/canceled task. The affected row count is returned
    ///     so callers can tell whether the row was recoverable.
    /// </summary>
    Task<int> RequeueStrandedTaskAsync(int taskId, CancellationToken cancellationToken = default);
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

        // Atomic compare-and-set: only a row that is still Pending may be claimed. Doing the check
        // and the update in a single guarded statement (instead of read-then-SaveChanges) means two
        // concurrent claimers cannot both observe Pending and both succeed, which would run the task
        // twice. A missing or already-claimed row affects no rows and yields false.
        int affected = await dbContext
            .PersistedTasks.Where(t => t.Id == taskId && t.Status == PersistedTaskStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.Status, PersistedTaskStatus.Processing),
                cancellationToken
            );

        return affected > 0;
    }

    public async Task<int> CompleteTaskAsync(
        int taskId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        DateTime? processedAt = DateTime.UtcNow;
        // Terminal transitions are guarded: a late or duplicate callback must not overwrite a
        // terminal state. Only tasks that are still Pending/Processing may complete. The returned
        // row count tells the caller whether the transition actually happened.
        return await dbContext
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

    public async Task<int> FailTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only a still-active task may fail, and the retry count is incremented in the same
        // conditional update so duplicate failure callbacks cannot inflate it. The returned row
        // count tells the caller whether the transition actually happened.
        return await dbContext
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

    public async Task<int> CancelTaskAsync(
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

        // Do not overwrite a terminal state: a late cancel after completion/failure is a no-op. The
        // returned row count tells the caller whether the transition actually happened.
        return await dbContext
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

    public async Task<int> RequeueStrandedTaskAsync(
        int taskId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only a still-active task may be returned to Pending. A terminal row is the source of
        // truth and must not be overwritten by a failed claim/recovery. The retry count is left
        // untouched: infrastructure recoveries do not consume the task's RetryFor budget.
        return await dbContext
            .PersistedTasks.Where(t =>
                t.Id == taskId
                && (
                    t.Status == PersistedTaskStatus.Pending
                    || t.Status == PersistedTaskStatus.Processing
                )
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.Status, PersistedTaskStatus.Pending),
                cancellationToken
            );
    }
}
