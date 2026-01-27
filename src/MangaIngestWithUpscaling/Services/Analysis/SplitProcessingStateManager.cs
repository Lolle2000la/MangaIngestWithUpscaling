using AutoRegisterInject;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.Analysis;

/// <summary>
/// Implements ISplitProcessingStateManager to handle consistent state management
/// for chapter split processing, ensuring proper initialization and versioning.
/// </summary>
[RegisterScoped]
public class SplitProcessingStateManager(
    ApplicationDbContext dbContext,
    ILogger<SplitProcessingStateManager> logger
) : ISplitProcessingStateManager
{
    private ApplicationDbContext GetContext(ApplicationDbContext? context) => context ?? dbContext;

    public async Task<ChapterSplitProcessingState> GetOrCreateStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await ctx.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapterId,
            cancellationToken
        );

        if (state == null)
        {
            state = new ChapterSplitProcessingState
            {
                ChapterId = chapterId,
                LastProcessedDetectorVersion = 0,
                LastAppliedDetectorVersion = 0,
                Status = SplitProcessingStatus.Pending,
                ModifiedAt = DateTime.UtcNow,
            };
            ctx.ChapterSplitProcessingStates.Add(state);
            await ctx.SaveChangesAsync(cancellationToken);

            logger.LogDebug(
                "Created new split processing state for chapter {ChapterId}",
                chapterId
            );
        }

        return state;
    }

    public async Task SetDetectedAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.LastProcessedDetectorVersion = detectorVersion;
        state.Status = SplitProcessingStatus.Detected;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Set split processing state for chapter {ChapterId} to Detected (version {Version})",
            chapterId,
            detectorVersion
        );
    }

    public async Task SetNoSplitsFoundAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.LastProcessedDetectorVersion = detectorVersion;
        state.Status = SplitProcessingStatus.NoSplitsFound;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Set split processing state for chapter {ChapterId} to NoSplitsFound (version {Version})",
            chapterId,
            detectorVersion
        );
    }

    public async Task SetAppliedAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        // Ensure LastProcessedDetectorVersion is set if not already
        if (state.LastProcessedDetectorVersion == 0)
        {
            state.LastProcessedDetectorVersion = detectorVersion;
        }

        state.Status = SplitProcessingStatus.Applied;
        state.LastAppliedDetectorVersion = detectorVersion;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Set split processing state for chapter {ChapterId} to Applied (version {Version})",
            chapterId,
            detectorVersion
        );
    }

    public async Task SetFailedAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.Status = SplitProcessingStatus.Failed;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug("Set split processing state for chapter {ChapterId} to Failed", chapterId);
    }

    public async Task SetProcessingAsync(
        int chapterId,
        int detectorVersion,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.LastProcessedDetectorVersion = detectorVersion;
        state.Status = SplitProcessingStatus.Processing;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Set split processing state for chapter {ChapterId} to Processing (version {Version})",
            chapterId,
            detectorVersion
        );
    }

    public async Task<ChapterSplitProcessingState?> GetStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        return await ctx.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapterId,
            cancellationToken
        );
    }

    public async Task UpdateStatusAsync(
        int chapterId,
        SplitProcessingStatus newStatus,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.Status = newStatus;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Updated split processing state for chapter {ChapterId} to {Status}",
            chapterId,
            newStatus
        );
    }

    public async Task ResetToPendingAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await GetOrCreateStateAsync(chapterId, context, cancellationToken);

        state.Status = SplitProcessingStatus.Pending;
        state.LastProcessedDetectorVersion = 0;
        state.LastAppliedDetectorVersion = 0;
        state.ModifiedAt = DateTime.UtcNow;

        ctx.ChapterSplitProcessingStates.Update(state);
        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Reset split processing state for chapter {ChapterId} to Pending",
            chapterId
        );
    }

    public async Task DeleteStateAsync(
        int chapterId,
        ApplicationDbContext? context = null,
        CancellationToken cancellationToken = default
    )
    {
        var ctx = GetContext(context);
        var state = await ctx.ChapterSplitProcessingStates.FirstOrDefaultAsync(
            s => s.ChapterId == chapterId,
            cancellationToken
        );

        if (state != null)
        {
            ctx.ChapterSplitProcessingStates.Remove(state);
            await ctx.SaveChangesAsync(cancellationToken);

            logger.LogDebug("Deleted split processing state for chapter {ChapterId}", chapterId);
        }
    }
}
