namespace MangaIngestWithUpscaling.Services.Upscaling;

/// <summary>
/// Periodically removes stale page-spool sessions and cached page contexts. Doing this on a timer
/// keeps the per-RPC handlers from paying for a filesystem sweep on every manifest.
/// </summary>
public sealed class PageSpoolSweepService(
    PageStreamSpool spool,
    PageContextCache cache,
    ILogger<PageSpoolSweepService> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep once at startup so roots left by a previous process are removed promptly, not an
        // hour later.
        Sweep();

        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        { /* shutting down */
        }
    }

    private void Sweep()
    {
        try
        {
            spool.SweepStale(Retention);
            cache.Sweep(Retention);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Page spool sweep failed.");
        }
    }
}
