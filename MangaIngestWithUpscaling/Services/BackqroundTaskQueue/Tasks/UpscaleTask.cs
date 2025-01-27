using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks
{
    public class UpscaleTask : BaseTask
    {
        public override string TaskFriendlyName => "Upscale manga";

        public int UpscalingQueueEntryId { get; set; }

        public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var dbContext = services.GetRequiredService<ApplicationDbContext>();
            var upscalingQueueEntry = await dbContext.UpscalingQueueEntries.FindAsync(UpscalingQueueEntryId);

            throw new NotImplementedException();
        }
    }
}
