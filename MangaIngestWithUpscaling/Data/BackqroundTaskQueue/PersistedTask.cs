using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Data.BackqroundTaskQueue
{
    public class PersistedTask
    {
        public int Id { get; set; }
        public BaseTask Data { get; set; } = null!;
        public PersistedTaskStatus Status { get; set; } = PersistedTaskStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int RetryCount { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}
