using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class UpscaleTask : BaseTask
{
    public override string TaskFriendlyName => FriendlyEntryName;

    public int UpscalerProfileId { get; set; }
    public int ChapterId { get; set; }

    private string FriendlyEntryName { get; set; } = string.Empty;

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var chapter = await dbContext.Chapters.FirstOrDefaultAsync(
            c => c.Id == ChapterId, cancellationToken: cancellationToken);
        var upscalerProfile = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
            c => c.Id == UpscalerProfileId, cancellationToken: cancellationToken);

        if (chapter == null || upscalerProfile == null)
        {
            throw new InvalidOperationException("Chapter or upscaler profile not found.");
        }

        FriendlyEntryName = $"Upscaling {chapter.FileName} with {upscalerProfile.Name}";

        throw new NotImplementedException();
    }
}
