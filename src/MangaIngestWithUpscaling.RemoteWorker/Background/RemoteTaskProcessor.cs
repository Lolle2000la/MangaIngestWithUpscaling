using System.Threading.Channels;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using CompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using ScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerMethod = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Claims tasks from the server and runs each one as a page stream. The worker only speaks the
/// page-streaming protocol: upscale, repair and split-detection tasks are streamed page by page and
/// assembled server-side. Whole-CBZ transfers are no longer supported.
/// </summary>
public class RemoteTaskProcessor(IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    private volatile bool _fetchInProgress;

    // Consecutive soft (transient/restart) failures per task, so a deterministically-failing task is
    // escalated to a reported failure instead of cycling forever — even if other tasks fail in
    // between (a single "last task" counter could be reset by an interleaved task).
    private readonly Dictionary<int, int> _softFailureCounts = new();

    private int _streamingTaskIdValue = -1;

    /// <summary>
    /// Id of the task the streaming loop is currently processing, or <c>null</c>. Used to avoid
    /// re-claiming the in-flight task and to time the next claim.
    /// </summary>
    private int? StreamingTaskId
    {
        get
        {
            int value = Volatile.Read(ref _streamingTaskIdValue);
            return value < 0 ? null : value;
        }
        set => Volatile.Write(ref _streamingTaskIdValue, value ?? -1);
    }

    private Channel<bool>? _fetchSignals;
    private Channel<StreamingItem>? _toStream;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        logger.LogInformation("Successfully connected to server and waiting for work.");

        _toStream = Channel.CreateBounded<StreamingItem>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        _fetchSignals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            }
        );

        _fetchSignals.Writer.TryWrite(true);

        Task fetchTask = FetchLoop(stoppingToken);
        Task streamingTask = StreamingLoop(stoppingToken);

        await Task.WhenAll(fetchTask, streamingTask);
    }

    private static bool IsTransient(StatusCode code) =>
        code
            is StatusCode.Unavailable
                or StatusCode.DeadlineExceeded
                or StatusCode.Cancelled
                // Unimplemented is a version-skew signal (a removed/changed RPC), not a deterministic
                // failure: treat it as transient so it does not burn the retry budget.
                or StatusCode.Unimplemented
                // Aborted/ResourceExhausted are retryable transport/backpressure conditions, and
                // Unknown is what an unhandled server-side exception produces — treating it as
                // transient avoids turning a recoverable handler error into spool deletion.
                or StatusCode.Aborted
                or StatusCode.ResourceExhausted
                or StatusCode.Unknown;

    /// <summary>
    /// How many consecutive soft (transient/restart) failures a task may accumulate before the worker
    /// reports it as a hard failure. The server's dead-task reaper requeues without consuming the
    /// retry budget, so without a cap a deterministically-failing task would cycle forever.
    /// </summary>
    private const int MaxConsecutiveSoftFailures = 5;

    /// <summary>
    /// A restart is the "no spool on this replica / identity changed" signal, which a misconfigured
    /// deployment can produce indefinitely; use a much larger cap so it eventually surfaces without
    /// turning a transient infrastructure issue into data loss.
    /// </summary>
    private const int MaxConsecutiveRestarts = 100;

    /// <summary>How a page-streaming failure should be handled.</summary>
    public enum StreamingFailureKind
    {
        /// <summary>A transport blip; requeue without reporting (the spool is preserved).</summary>
        Transient,

        /// <summary>A non-terminal rejection; restart the chapter without reporting.</summary>
        Restart,

        /// <summary>A deterministic failure; report it (which clears the spool).</summary>
        Permanent,
    }

    /// <summary>
    /// Classifies a page-streaming failure. The whole exception chain is inspected, not just the
    /// innermost: grpc-dotnet keeps the transport error in <see cref="Exception.InnerException"/>, so
    /// <see cref="Exception.GetBaseException"/> would return (for example) a SocketException and miss
    /// the <see cref="RpcException"/> carrying the status code. A restart or transient signal anywhere
    /// in the chain wins over a permanent classification.
    /// </summary>
    public static StreamingFailureKind ClassifyStreamingFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PageStreamRestartException)
            {
                return StreamingFailureKind.Restart;
            }

            if (current is UpscaleWorkerCrashedException)
            {
                // A crash (OOM/CUDA fault) is recoverable: respawn the worker and resume. Reporting it
                // would drop the already-spooled pages and consume the retry budget.
                return StreamingFailureKind.Transient;
            }

            if (current is TimeoutException)
            {
                // A wedged/hung worker (a cold model load or a transient CUDA stall past the scaled
                // inactivity timeout) is as recoverable as a crash: respawn and resume rather than
                // discarding the already-upscaled pages.
                return StreamingFailureKind.Transient;
            }

            if (current is RpcException rpc)
            {
                // The server signals "the chapter/profile/engine changed, restart" with
                // FailedPrecondition (e.g. GetPages after a manifest). That is a restart, not a
                // failure.
                if (rpc.StatusCode == StatusCode.FailedPrecondition)
                {
                    return StreamingFailureKind.Restart;
                }

                if (IsTransient(rpc.StatusCode))
                {
                    return StreamingFailureKind.Transient;
                }
            }
        }

        return StreamingFailureKind.Permanent;
    }

    /// <summary>
    /// Surfaces a page-streaming failure. A transient or restart failure is only logged: the worker
    /// lets the keep-alive lapse so the server requeues the task with its spool intact. A permanent
    /// one is reported, which makes the server clear the spool. Extracted from the streaming loop so
    /// the spool-preservation property can be asserted without running the loop.
    ///
    /// <paramref name="consecutiveSoftFailures"/> caps how long a deterministically-failing task can
    /// cycle: the server's dead-task reaper requeues without consuming the retry budget, so a failure
    /// that is permanently bad but classifies soft would otherwise loop forever. Once the cap is
    /// reached the failure is reported (terminal), surfacing the task.
    /// </summary>
    public static async Task HandleStreamingFailureAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        Exception ex,
        ILogger logger,
        CancellationToken stoppingToken,
        int consecutiveSoftFailures = 1
    )
    {
        StreamingFailureKind kind = ClassifyStreamingFailure(ex);
        if (kind is StreamingFailureKind.Transient or StreamingFailureKind.Restart)
        {
            int cap =
                kind == StreamingFailureKind.Restart
                    ? MaxConsecutiveRestarts
                    : MaxConsecutiveSoftFailures;
            if (consecutiveSoftFailures < cap)
            {
                // A transport blip or a non-terminal rejection must not consume the retry budget or
                // drop the spool: reporting a failure makes the server delete the already-upscaled
                // pages. Let the keep-alive lapse instead, so the server requeues the task with the
                // spool intact and the worker resumes from the first missing page.
                logger.LogWarning(
                    ex,
                    "Task {TaskId} was interrupted during page streaming; letting the server requeue it with the spool intact.",
                    taskId
                );
                return;
            }

            logger.LogError(
                ex,
                "Task {TaskId} failed after {Count} consecutive soft failures; reporting it as terminal.",
                taskId,
                consecutiveSoftFailures
            );
        }
        else
        {
            logger.LogError(ex, "Task {TaskId} failed during page streaming.", taskId);
        }

        try
        {
            await client.ReportTaskFailedAsync(
                new ReportTaskFailedRequest { TaskId = taskId, ErrorMessage = ex.Message },
                deadline: DateTime.UtcNow.AddSeconds(15),
                cancellationToken: stoppingToken
            );
        }
        catch (Exception rpcEx)
        {
            logger.LogWarning(
                rpcEx,
                "Failed to report page-streaming failure for {TaskId}",
                taskId
            );
        }
    }

    /// <summary>
    /// Reserves tasks from the server and hands them to the streaming loop. Only upscale and
    /// split-detection tasks are delegated to workers.
    /// </summary>
    private async Task FetchLoop(CancellationToken stoppingToken)
    {
        using var dispatcherTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        bool serverAvailable = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_fetchSignals is null || _toStream is null)
                {
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                bool _ = await _fetchSignals.Reader.ReadAsync(stoppingToken);
                if (_fetchInProgress)
                {
                    // Coalesce signals while a fetch is in progress
                    continue;
                }

                _fetchInProgress = true;

                while (_fetchSignals.Reader.TryRead(out _)) { }

                using var scope = serviceScopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<
                    ILogger<RemoteTaskProcessor>
                >();
                var client =
                    scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();

                if (!await _toStream.Writer.WaitToWriteAsync(stoppingToken))
                {
                    _fetchInProgress = false;
                    continue;
                }

                UpscaleTaskDelegationResponse resp;
                while (true)
                {
                    try
                    {
                        resp = await client.RequestUpscaleTaskWithHintAsync(
                            new RequestTaskRequest { Prefetch = true },
                            deadline: DateTime.UtcNow.AddSeconds(15),
                            cancellationToken: stoppingToken
                        );
                        serverAvailable = true;
                    }
                    catch (RpcException e)
                        when (e.StatusCode is StatusCode.NotFound or StatusCode.Unavailable)
                    {
                        if (serverAvailable && e.StatusCode == StatusCode.Unavailable)
                        {
                            logger.LogWarning(
                                "Server is currently unavailable; will retry shortly."
                            );
                            serverAvailable = false;
                        }

                        await dispatcherTimer.WaitForNextTickAsync(stoppingToken);
                        continue;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to request upscale task for prefetch.");
                        _fetchInProgress = false;
                        throw;
                    }

                    if (StreamingTaskId == resp.TaskId)
                    {
                        logger.LogDebug(
                            "FetchLoop: got in-flight task {taskId}; retrying shortly",
                            resp.TaskId
                        );
                        try
                        {
                            await Task.Delay(200, stoppingToken);
                        }
                        catch { }

                        continue;
                    }

                    break;
                }

                if (resp is null || resp.TaskId == -1)
                {
                    _fetchInProgress = false;
                    await dispatcherTimer.WaitForNextTickAsync(stoppingToken);
                    _fetchSignals.Writer.TryWrite(true);
                    continue;
                }

                int taskId = resp.TaskId;
                logger.LogInformation(
                    "Received task {TaskId} of type {TaskType} from server.",
                    taskId,
                    resp.TaskType
                );

                if (resp.TaskType is not (TaskType.Upscale or TaskType.SplitDetection))
                {
                    // Only upscale and split-detection tasks are delegated to workers. The task was
                    // already claimed by RequestUpscaleTaskWithHint, so let its keep-alive lapse
                    // (the reaper requeues it) and back off instead of spinning as fast as the
                    // server answers.
                    logger.LogWarning(
                        "Ignoring task {TaskId} of unsupported type {TaskType}.",
                        taskId,
                        resp.TaskType
                    );
                    _fetchInProgress = false;
                    await dispatcherTimer.WaitForNextTickAsync(stoppingToken);
                    _fetchSignals.Writer.TryWrite(true);
                    continue;
                }

                UpscalerProfile profile = GetProfileFromResponse(resp.UpscalerProfile);

                var persistentKeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken
                );
                Task persistentKeepAliveTask = RunKeepAliveLoop(
                    persistentKeepAliveCts,
                    () => taskId,
                    id => new KeepAliveRequest { TaskId = id, Prefetch = true }
                );

                // Hand the reserved task to the streaming loop; it owns the keep-alive from here.
                var streaming = new StreamingItem(
                    taskId,
                    resp.TaskType,
                    profile,
                    persistentKeepAliveCts,
                    persistentKeepAliveTask
                );
                try
                {
                    await _toStream.Writer.WriteAsync(streaming, stoppingToken);
                }
                catch
                {
                    await persistentKeepAliveCts.CancelAsync();
                    try
                    {
                        await persistentKeepAliveTask;
                    }
                    catch { }
                    persistentKeepAliveCts.Dispose();
                    throw;
                }

                _fetchInProgress = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                _fetchInProgress = false;
                _fetchSignals!.Writer.TryWrite(false);
                // Soft failure; wait a bit before next signal consumption
                try
                {
                    await Task.Delay(500, stoppingToken);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private async Task StreamingLoop(CancellationToken stoppingToken)
    {
        if (_toStream is null)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        var client =
            scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var pageStreamClient = scope.ServiceProvider.GetRequiredService<PageStreamClient>();

        while (!stoppingToken.IsCancellationRequested)
        {
            StreamingItem item;
            try
            {
                item = await _toStream.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            StreamingTaskId = item.TaskId;

            if (item.PersistentKeepAliveCts.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Task {TaskId} was cancelled prior to page streaming, skipping.",
                    item.TaskId
                );
                await item.PersistentKeepAliveCts.CancelAsync();
                try
                {
                    await item.PersistentKeepAliveTask;
                }
                catch { }
                item.PersistentKeepAliveCts.Dispose();
                StreamingTaskId = null;
                // Re-signal the fetch loop, or it would block on _fetchSignals forever and the
                // worker would stop claiming tasks.
                _fetchSignals?.Writer.TryWrite(true);
                continue;
            }

            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                item.PersistentKeepAliveCts.Token
            );

            try
            {
                if (item.TaskType == TaskType.SplitDetection)
                {
                    logger.LogInformation(
                        "Page-streaming split detection for task {TaskId}.",
                        item.TaskId
                    );
                    await pageStreamClient.RunDetectionAsync(client, item.TaskId, streamCts.Token);
                }
                else
                {
                    logger.LogInformation("Page-streaming task {TaskId}.", item.TaskId);
                    await pageStreamClient.RunAsync(
                        client,
                        item.TaskId,
                        item.Profile,
                        streamCts.Token
                    );
                }

                // Completed without a soft failure; clear this task's counter.
                _softFailureCounts.Remove(item.TaskId);
            }
            catch (OperationCanceledException)
            {
                // Normal user interruption or task cancellation.
            }
            catch (Exception ex)
            {
                _softFailureCounts.TryGetValue(item.TaskId, out int softFailures);
                softFailures++;
                // Bound the map: once a task has reached the largest cap it is being reported, so its
                // count no longer matters.
                if (softFailures >= MaxConsecutiveRestarts)
                {
                    _softFailureCounts.Remove(item.TaskId);
                }
                else
                {
                    _softFailureCounts[item.TaskId] = softFailures;
                }

                await HandleStreamingFailureAsync(
                    client,
                    item.TaskId,
                    ex,
                    logger,
                    stoppingToken,
                    softFailures
                );
            }
            finally
            {
                await item.PersistentKeepAliveCts.CancelAsync();
                try
                {
                    await item.PersistentKeepAliveTask;
                }
                catch { }
                item.PersistentKeepAliveCts.Dispose();
                StreamingTaskId = null;
                _fetchSignals?.Writer.TryWrite(true);
            }
        }
    }

    internal static UpscalerProfile GetProfileFromResponse(
        Api.Upscaling.UpscalerProfile? upscalerProfile
    )
    {
        if (upscalerProfile == null)
        {
            return new UpscalerProfile
            {
                Name = "None",
                CompressionFormat = CompressionFormat.Webp,
                Quality = 75,
                ScalingFactor = ScaleFactor.OneX,
                UpscalerMethod = UpscalerMethod.MangaJaNai,
            };
        }

        return new UpscalerProfile
        {
            CompressionFormat = upscalerProfile.CompressionFormat switch
            {
                Api.Upscaling.CompressionFormat.Webp => CompressionFormat.Webp,
                Api.Upscaling.CompressionFormat.Png => CompressionFormat.Png,
                Api.Upscaling.CompressionFormat.Jpg => CompressionFormat.Jpg,
                Api.Upscaling.CompressionFormat.Avif => CompressionFormat.Avif,
                _ => throw new InvalidOperationException("Unknown compression format."),
            },
            Name = upscalerProfile.Name,
            Quality = upscalerProfile.Quality,
            ScalingFactor = upscalerProfile.ScalingFactor switch
            {
                Api.Upscaling.ScaleFactor.OneX => ScaleFactor.OneX,
                Api.Upscaling.ScaleFactor.TwoX => ScaleFactor.TwoX,
                Api.Upscaling.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                Api.Upscaling.ScaleFactor.FourX => ScaleFactor.FourX,
                _ => throw new InvalidOperationException("Unknown scaling factor."),
            },
            UpscalerMethod = upscalerProfile.UpscalerMethod switch
            {
                Api.Upscaling.UpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                _ => throw new InvalidOperationException("Unknown upscaler method."),
            },
        };
    }

    private Task RunKeepAliveLoop(
        CancellationTokenSource cts,
        Func<int?> taskIdProvider,
        Func<int, KeepAliveRequest> requestFactory
    )
    {
        return Task.Run(
            async () =>
            {
                using IServiceScope scope = serviceScopeFactory.CreateScope();
                var client =
                    scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        int? id = taskIdProvider();
                        if (!id.HasValue)
                        {
                            break;
                        }

                        KeepAliveResponse? ka = await client.KeepAliveAsync(
                            requestFactory(id.Value),
                            deadline: DateTime.UtcNow.AddSeconds(10),
                            cancellationToken: cts.Token
                        );
                        if (!ka.IsAlive)
                        {
                            await cts.CancelAsync();
                            break;
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                    {
                        await cts.CancelAsync();
                        break;
                    }
                    catch { }

                    try
                    {
                        await timer.WaitForNextTickAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            },
            cts.Token
        );
    }

    private sealed record StreamingItem(
        int TaskId,
        TaskType TaskType,
        UpscalerProfile Profile,
        CancellationTokenSource PersistentKeepAliveCts,
        Task PersistentKeepAliveTask
    );
}
