using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel;

namespace MangaIngestWithUpscaling.Data.BackqroundTaskQueue;

public class PersistedTask
{
    public int Id { get; set; }
    public BaseTask Data { get; set; } = null!;
    public PersistedTaskStatus Status { get; set; } = PersistedTaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Order { get; set; }

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
}
