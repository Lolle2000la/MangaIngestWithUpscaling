using System.ComponentModel.DataAnnotations.Schema;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

public class PersistedTask
{
    public int Id { get; set; }
    public BaseTask Data { get; set; } = null!;
    public PersistedTaskStatus Status { get; set; } = PersistedTaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// The last time the task was kept alive.
    /// Only used for network-distributed tasks.
    /// </summary>
    [NotMapped]
    public DateTime LastKeepAlive { get; set; } = DateTime.UtcNow;

    // override object.Equals
    public override bool Equals(object? obj)
    {
        if (obj is not null and PersistedTask task)
        {
            return GetHashCode() == task.GetHashCode();
        }

        return false;
    }

    // override object.GetHashCode
    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Status, CreatedAt, ProcessedAt, RetryCount);
    }

    /// <summary>
    /// Gets the sort priority for the task status.
    /// Lower values appear first in the UI.
    /// </summary>
    public int GetStatusSortPriority()
    {
        return Status switch
        {
            PersistedTaskStatus.Completed
            or PersistedTaskStatus.Canceled
            or PersistedTaskStatus.Failed => 0,
            PersistedTaskStatus.Processing => 1,
            PersistedTaskStatus.Pending => 2,
            _ => 3,
        };
    }
}
