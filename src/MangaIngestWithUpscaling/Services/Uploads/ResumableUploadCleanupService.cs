using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.Uploads;

/// <summary>
/// Periodically removes upload chunk directories that have been idle for longer than
/// <see cref="UploadsConfig.ChunkRetention"/>, bounding disk usage from uploads that are abandoned
/// or cancelled without ever reporting a failure.
/// </summary>
public class ResumableUploadCleanupService(
    ResumableUploadStore store,
    IOptions<UploadsConfig> config,
    ILogger<ResumableUploadCleanupService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UploadsConfig options = config.Value;
        TimeSpan interval =
            options.CleanupInterval > TimeSpan.Zero
                ? options.CleanupInterval
                : TimeSpan.FromHours(1);
        TimeSpan retention =
            options.ChunkRetention > TimeSpan.Zero
                ? options.ChunkRetention
                : TimeSpan.FromHours(24);

        await SweepAsync(retention, stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepAsync(retention, stoppingToken);
        }
    }

    private async Task SweepAsync(TimeSpan retention, CancellationToken stoppingToken)
    {
        try
        {
            int removed = await Task.Run(() => store.SweepStaleUploads(retention), stoppingToken);
            if (removed > 0)
            {
                logger.LogInformation(
                    "Removed {count} stale resumable-upload directories older than {retention}.",
                    removed,
                    retention
                );
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sweep stale resumable-upload directories.");
        }
    }
}
