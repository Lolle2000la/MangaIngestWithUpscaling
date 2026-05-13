using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Background task to update existing filtered images with perceptual hashes
/// </summary>
public class UpdatePerceptualHashesTask : BaseTask
{
    public override async Task ProcessAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken
    )
    {
        using var scope = serviceProvider.CreateScope();
        await using var dbContext = await scope
            .ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        var migrationService =
            scope.ServiceProvider.GetRequiredService<PerceptualHashMigrationService>();
        await migrationService.UpdateExistingFilteredImagesAsync(dbContext, cancellationToken);
    }
}
