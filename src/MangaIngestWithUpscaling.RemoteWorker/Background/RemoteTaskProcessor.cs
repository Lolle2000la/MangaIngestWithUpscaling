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
        code is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled;

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
    /// Classifies a page-streaming failure. <see cref="Exception.GetBaseException"/> unwraps the
    /// producer's "failed to stream" wrapper so a transient fetch failure is not misreported as a
    /// hard failure, which would delete the spool.
    /// </summary>
    public static StreamingFailureKind ClassifyStreamingFailure(Exception ex)
    {
        Exception baseEx = ex.GetBaseException();
        if (baseEx is PageStreamRestartException)
        {
            return StreamingFailureKind.Restart;
        }

        if (baseEx is UpscaleWorkerCrashedException)
        {
            // A crash (OOM/CUDA fault) is recoverable: respawn the worker and resume. Reporting it
            // would drop the already-spooled pages and consume the retry budget.
            return StreamingFailureKind.Transient;
        }

        if (baseEx is RpcException rpc)
        {
            // The server signals "the chapter/profile/engine changed, restart" with FailedPrecondition
            // (e.g. GetPages after a manifest). That is a restart, not a failure.
            if (rpc.StatusCode == StatusCode.FailedPrecondition)
            {
                return StreamingFailureKind.Restart;
            }

            if (IsTransient(rpc.StatusCode))
            {
                return StreamingFailureKind.Transient;
            }
        }

        return StreamingFailureKind.Permanent;
    }

    /// <summary>
    /// Surfaces a page-streaming failure. A transient or restart failure is only logged: the worker
    /// lets the keep-alive lapse so the server requeues the task with its spool intact. A permanent
    /// one is reported, which makes the server clear the spool. Extracted from the streaming loop so
    /// the spool-preservation property can be asserted without running the loop.
    /// </summary>
    public static async Task HandleStreamingFailureAsync(
        UpscalingService.UpscalingServiceClient client,
        int taskId,
        Exception ex,
        ILogger logger,
        CancellationToken stoppingToken
    )
    {
        if (
            ClassifyStreamingFailure(ex)
            is StreamingFailureKind.Transient
                or StreamingFailureKind.Restart
        )
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

        logger.LogError(ex, "Task {TaskId} failed during page streaming.", taskId);
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
        var dispatcherTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
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
                    // Only upscale and split-detection tasks are delegated to workers.
                    logger.LogWarning(
                        "Ignoring task {TaskId} of unsupported type {TaskType}.",
                        taskId,
                        resp.TaskType
                    );
                    _fetchInProgress = false;
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
            }
            catch (OperationCanceledException)
            {
                // Normal user interruption or task cancellation.
            }
            catch (Exception ex)
            {
                await HandleStreamingFailureAsync(client, item.TaskId, ex, logger, stoppingToken);
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
