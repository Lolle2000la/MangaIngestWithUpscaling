using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class UpscaleTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<UpscaleTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService,
    IPreprocessedInputCache preprocessedCache,
    IQuietHoursGate quietHours
) : BackgroundTaskProcessorBase(taskQueue, scopeFactory, logger, taskPersistenceService)
{
    private ChannelReader<object> Reader => TaskQueue.UpscaleReader;
    private ChannelReader<PersistedTask> ReroutedReader => TaskQueue.ReroutedUpscaleReader;
    private readonly PrefetchCoordinator _coordinator = new();

    // Tracks whether the current pause episode has been announced, so a window that lasts hours is
    // logged once on entry and once on exit instead of on every poll.
    private bool _quietHoursPaused;

    protected override string ProcessingFailedLogMessage => "Upscale task {TaskId} failed";

    protected override void OnTaskAcquired(PersistedTask task)
    {
        _coordinator.Reset();
        // Reset any phase left over from a previous attempt so a stale "finalizing" doesn't
        // bleed into this run's status.
        task.Data.Progress.Phase = null;
    }

    protected override void OnProgressChanged(PersistedTask task)
    {
        // Trigger the next task's prefetch when the predictor says it's time.
        if (
            _coordinator.OnProgress(
                task.Data.Progress.Total,
                task.Data.Progress.Current,
                task.Data.Progress.Phase
            )
        )
        {
            _ = PrefetchNextAsync(task, ServiceStoppingToken);
        }
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (upscalerConfig.Value.RemoteOnly)
        {
            // The local ML backend is disabled, so genuine upscaling must not run here. However,
            // the distributed processor reroutes tasks it cannot delegate (notably
            // RenameUpscaledChaptersSeriesTask) to this processor. Those do not need the ML backend,
            // so drain only the rerouted channel; never read the main upscale channel, whose
            // consumers in RemoteOnly are the remote workers.
            while (!stoppingToken.IsCancellationRequested)
            {
                PersistedTask rerouted;
                try
                {
                    // WaitToReadAsync does not consume, so parking after it (quiet hours) holds
                    // nothing and loses nothing — the task stays in the channel if the process stops.
                    if (!await ReroutedReader.WaitToReadAsync(stoppingToken))
                    {
                        break;
                    }

                    // Rerouted tasks are locally-required upscale work (they were delegated here
                    // because a remote worker cannot run them), so they are "new work" too and must
                    // respect the window. Gating here, after the wait, is what catches a host that
                    // was already parked when the window opened.
                    await WaitForQuietHoursAsync(stoppingToken);

                    rerouted = await ReroutedReader.ReadAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                CancellationTokenSource taskStoppingToken = BeginCurrentTask(
                    rerouted,
                    stoppingToken
                );

                try
                {
                    await ProcessTaskAsync(rerouted, taskStoppingToken.Token);
                }
                finally
                {
                    DisposeCurrentStoppingToken(taskStoppingToken);
                }
            }

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Park at the top so an idle host waits out the window at its start rather than polling,
            // and so a window that opened while the loop was between tasks is honoured before the
            // channel wait below.
            await WaitForQuietHoursAsync(stoppingToken);

            PersistedTask? task = null;

            // Use a linked CTS to ensure that the "losing" waiter is cancelled and removed from its
            // channel when one of them completes. Neither wait consumes an item, which is what lets
            // the gate below run with nothing held.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task<bool> reroutedWait = ReroutedReader.WaitToReadAsync(linkedCts.Token).AsTask();
            Task<bool> signalWait = Reader.WaitToReadAsync(linkedCts.Token).AsTask();

            Task<bool> completed = await Task.WhenAny(reroutedWait, signalWait);

            // The authoritative check: the park above happened before an unbounded wait, so a window
            // that opened while this loop was parked must be honoured here, before anything is read or
            // claimed. Nothing has been taken off either channel yet, so a task enqueued during the
            // window stays queued with its signal intact.
            if (quietHours.Current is { IsQuiet: true } quiet)
            {
                LogQuietHoursState(quiet);
                await linkedCts.CancelAsync();
                continue;
            }

            bool reroutedReady = completed == reroutedWait && await reroutedWait;
            bool signalReady = completed != reroutedWait && await signalWait;

            // Cancel the CTS to remove any abandoned waiter from the channels
            await linkedCts.CancelAsync();

            if (!reroutedReady && !signalReady)
            {
                // Both channels report completion. They are unbounded and never completed, so this
                // means the reader itself is finished; sitting in the loop would spin.
                break;
            }

            if (reroutedReady)
            {
                // Priority 1: rerouted tasks (already claimed or specialized)
                task = await ReroutedReader.ReadAsync(stoppingToken);
            }
            else if (Reader.TryRead(out _))
            {
                task = TaskQueue.DequeueUpscale();
            }

            if (task == null)
            {
                continue;
            }

            CancellationTokenSource taskStoppingToken = BeginCurrentTask(task, stoppingToken);

            try
            {
                await ProcessTaskAsync(task, taskStoppingToken.Token);
            }
            finally
            {
                DisposeCurrentStoppingToken(taskStoppingToken);
            }
        }
    }

    /// <summary>
    ///     Holds the loop while the configured quiet-hours window is open, so no new upscale task is
    ///     dequeued and started. An upscale already being processed is untouched: the check happens
    ///     between tasks, never during one.
    /// </summary>
    private async Task WaitForQuietHoursAsync(CancellationToken stoppingToken)
    {
        QuietHoursState state = quietHours.Current;
        LogQuietHoursState(state);
        if (state.IsQuiet)
        {
            await quietHours.WaitUntilNotQuietAsync(stoppingToken);
        }
    }

    /// <summary>
    ///     Logs entering or leaving the window once per episode rather than on every poll. A schedule
    ///     with no end in sight says so by leaving the time out, instead of naming a resume that never
    ///     arrives.
    /// </summary>
    private void LogQuietHoursState(QuietHoursState state)
    {
        if (!state.IsQuiet)
        {
            if (_quietHoursPaused)
            {
                _quietHoursPaused = false;
                Logger.LogInformation("Task claiming resumed");
            }

            return;
        }

        if (_quietHoursPaused)
        {
            return;
        }

        _quietHoursPaused = true;
        if (state.ResumeAtLocal is TimeOnly resume)
        {
            Logger.LogInformation(
                "Task claiming paused by quiet hours until {ResumeTime}",
                resume.ToString("HH:mm", CultureInfo.InvariantCulture)
            );
        }
        else
        {
            Logger.LogInformation("Task claiming paused by quiet hours");
        }
    }

    protected override async Task<bool> TryAcquireTaskAsync(
        PersistedTask task,
        CancellationToken stoppingToken
    )
    {
        // A task that arrives already Processing was claimed and rerouted by the distributed
        // processor. A task from the main queue is Pending and must be claimed here.
        if (task.Status != PersistedTaskStatus.Processing)
        {
            // A same-chapter apply rewrites the original (and upscaled) CBZ this task streams from.
            // StandardTaskProcessor defers the apply under an upscale; this is the symmetric half, so
            // the two never run together. The chapter gate makes the guard and the claim atomic against
            // the apply side's guard.
            if (task.Data is IChapterTask chapterTask)
            {
                using IDisposable gate = await TaskQueue.AcquireChapterGateAsync(
                    chapterTask.ChapterId,
                    stoppingToken
                );
                if (await HasSameChapterApplyTaskAsync(chapterTask.ChapterId, stoppingToken))
                {
                    DeferTask(
                        task,
                        "a same-chapter ApplySplitsTask is in flight and is rewriting the chapter"
                    );
                    return false;
                }

                return await ClaimAsync(task, stoppingToken);
            }

            return await ClaimAsync(task, stoppingToken);
        }

        // Removal does not reach the local reroute channel, so the row may have been deleted
        // (or finalized/canceled) while the task sat there. Verify it is still in a state that
        // should be executed before running the work: a terminal row must not produce side
        // effects only for the subsequent guarded CompleteTaskAsync to affect no rows.
        using var checkScope = ScopeFactory.CreateScope();
        var dbContext = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        bool rowActive;
        try
        {
            rowActive = await dbContext.PersistedTasks.AnyAsync(
                t =>
                    t.Id == task.Id
                    && (
                        t.Status == PersistedTaskStatus.Pending
                        || t.Status == PersistedTaskStatus.Processing
                    ),
                stoppingToken
            );
        }
        catch (OperationCanceledException)
        {
            // The distributed processor claimed and rerouted this task, so it is not in
            // runningTasks and CancelCurrent could not reach it. The cancellation must not be
            // dropped here, or the row would stay Processing and out of every queue; apply the
            // intended cancel just as the claim-cancellation path does.
            Logger.LogInformation("Verification of rerouted task {TaskId} was canceled", task.Id);
            // The task is abandoned here (no retry is scheduled), so a pending counter must not linger.
            ForgetClaimAttempts(task.Id);
            await ApplyClaimCancellationAsync(task);
            return false;
        }
        catch (Exception ex)
        {
            // A transient verification failure is retried promptly (with a bounded backoff) so
            // it does not have to wait for the 10-minute periodic replayer, but a persistent
            // failure is left Pending after a bounded number of attempts so it cannot hot-loop
            // this local processor or strand its Processing row.
            Logger.LogError(
                ex,
                "Failed to verify rerouted task {TaskId}; returning it to Pending for retry",
                task.Id
            );
            await ReturnToPendingForReplayAsync(task);
            await RequeueTransientClaimFailureAsync(task);
            return false;
        }

        if (!rowActive)
        {
            Logger.LogInformation(
                "Skipping rerouted task {TaskId} because its database row is missing or no longer active.",
                task.Id
            );
            // The task is abandoned here (no retry is scheduled), so a pending counter must not linger.
            ForgetClaimAttempts(task.Id);
            return false;
        }

        return true;
    }

    private async Task<bool> HasSameChapterApplyTaskAsync(
        int chapterId,
        CancellationToken stoppingToken
    )
    {
        using IServiceScope scope = ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await ChapterConflictGuard.HasSameChapterApplyTaskAsync(
            dbContext,
            chapterId,
            stoppingToken
        );
    }

    /// <summary>
    /// Preprocesses the next pending upscale task in the queue, so its CPU-bound
    /// preprocessing overlaps with the current task's GPU-bound upscaling.
    /// </summary>
    private async Task PrefetchNextAsync(PersistedTask currentTask, CancellationToken stoppingToken)
    {
        try
        {
            PersistedTask? next = TaskQueue
                .GetUpscaleSnapshot()
                .FirstOrDefault(t =>
                    t.Id != currentTask.Id
                    && t.Status == PersistedTaskStatus.Pending
                    && t.Data is UpscaleTask
                );

            if (next is null)
            {
                return;
            }

            var upscaleTask = (UpscaleTask)next.Data;

            // Register the prefetch promise first so the consuming task awaits it instead of
            // racing with a fallback that would duplicate the work and leak the result.
            TaskCompletionSource<IPreprocessedInput?> completion = preprocessedCache.StartPrefetch(
                upscaleTask.ChapterId
            );
            try
            {
                var sw = Stopwatch.StartNew();
                using var scope = ScopeFactory.CreateScope();
                IPreprocessedInput? preprocessed = await upscaleTask.PreprocessForPrefetchAsync(
                    scope.ServiceProvider,
                    stoppingToken
                );
                sw.Stop();
                _coordinator.RecordPrefetch(sw.Elapsed);

                // If another processor (e.g. the distributed one) claimed this task while we
                // were preprocessing, don't hand over the result: the consuming task will never
                // run locally, so dispose it and let the consumer fall back to inline instead.
                if (next.Status != PersistedTaskStatus.Pending)
                {
                    preprocessed?.Dispose();
                    completion.TrySetResult(null);
                    return;
                }

                if (!completion.TrySetResult(preprocessed))
                {
                    // Another prefetch already completed this promise (double prefetch of the
                    // same chapter); reclaim the unused result.
                    preprocessed?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Prefetch of the next upscale task failed.");
                completion.TrySetResult(null);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the prefetch is best-effort.
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Prefetch of the next upscale task failed.");
        }
    }
}
