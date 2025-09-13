using MangaIngestWithUpscaling.Services.ImageFiltering;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Background task to update existing filtered images with perceptual hashes
/// </summary>
public class UpdatePerceptualHashesTask : BaseTask
{
    public override string TaskFriendlyName => "Update Perceptual Hashes for Filtered Images";

    public override async Task ProcessAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var migrationService = scope.ServiceProvider.GetRequiredService<PerceptualHashMigrationService>();
        await migrationService.UpdateExistingFilteredImagesAsync(cancellationToken);
    }
}