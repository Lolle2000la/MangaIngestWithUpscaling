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
        // Yield before sweeping so the sweep is not part of startup: the first await is what the host
        // waits on while starting, and a large spool tree must not delay the application coming up. The
        // sweep then runs on the continuation, well before the timer's first tick an hour later.
        await Task.Yield();

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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Reclaim this process's own root: SweepStale deliberately skips the live process's root, so
        // without this an empty (or leftover) root lingers until another process ages it out.
        try
        {
            spool.DeleteDirectory(spool.SpoolRoot);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove the page spool root on shutdown.");
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
