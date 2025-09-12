using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using System.Diagnostics;
using System.Threading.Channels;
using CompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using ScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerMethod = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    // Rolling stats across runs
    private readonly OnlineStats _downloadTimeStats = new();
    private readonly OnlineStats _perPageTimeStats = new();

    // IDs to coordinate exclusions and lifecycle
    private int? _currentTaskId; // Task currently being processed
    private volatile bool _fetchInProgress;
    private Channel<bool>? _fetchSignals;
    private string? _prefetchFilePath;
    private CancellationTokenSource? _prefetchKeepAliveCts;
    private Task? _prefetchKeepAliveTask;
    private DateTime _prefetchNextAllowedAt = DateTime.MinValue;
    private int _prefetchNoTaskStreak;
    private UpscalerProfile? _prefetchProfile;
    private Task? _prefetchTask;

    // Prefetch state
    private int? _prefetchTaskId;
    private Channel<ProcessedItem>? _toUpload;

    // Channel-based pipeline
    private Channel<FetchedItem>? _toUpscale;
    private int? _uploadInProgressTaskId; // Task whose upload is in progress

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        logger.LogInformation("Successfully connected to server and waiting for work.");

        // Initialize channels
        _toUpscale = Channel.CreateBounded<FetchedItem>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        _toUpload = Channel.CreateBounded<ProcessedItem>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        _fetchSignals = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true, SingleWriter = false
        });

        // Kick off an initial fetch
        _fetchSignals.Writer.TryWrite(true);

        // Start pipeline loops
        Task fetchTask = FetchLoop(stoppingToken);
        Task upscaleTask = UpscaleLoop(stoppingToken);
        Task uploadTask = UploadLoop(stoppingToken);

        await Task.WhenAll(fetchTask, upscaleTask, uploadTask);
    }

    // Fetch: waits for signals, reserves next task (prefetch), downloads CBZ, keeps reservation alive until handed to upscaler
    private async Task FetchLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_fetchSignals is null || _toUpscale is null)
                {
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                // Wait for a fetch signal
                bool _ = await _fetchSignals.Reader.ReadAsync(stoppingToken);
                if (_fetchInProgress)
                {
                    // Coalesce signals while a fetch is in progress
                    continue;
                }

                _fetchInProgress = true;

                // Drain any extra queued signals to coalesce into one fetch
                while (_fetchSignals.Reader.TryRead(out _)) { }

                using var scope = serviceScopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
                var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();

                // Ensure upscale queue has capacity before we reserve/download next task
                if (!await _toUpscale.Writer.WaitToWriteAsync(stoppingToken))
                {
                    _fetchInProgress = false;
                    continue;
                }

                // Reserve a task with prefetch hint; avoid current/uploading
                UpscaleTaskDelegationResponse? resp;
                while (true)
                {
                    resp = await client.RequestUpscaleTaskWithHintAsync(new RequestTaskRequest { Prefetch = true },
                        cancellationToken: stoppingToken);
                    if (resp.TaskId == -1)
                    {
                        // Nothing to fetch at the moment
                        _fetchInProgress = false;
                        break;
                    }

                    if ((_currentTaskId.HasValue && resp.TaskId == _currentTaskId.Value) ||
                        (_uploadInProgressTaskId.HasValue && resp.TaskId == _uploadInProgressTaskId.Value))
                    {
                        logger.LogDebug("FetchLoop: got in-flight task {taskId}; retrying shortly", resp.TaskId);
                        try { await Task.Delay(200, stoppingToken); }
                        catch { }

                        continue;
                    }

                    break;
                }

                if (resp is null || resp.TaskId == -1)
                {
                    continue;
                }

                _prefetchTaskId = resp.TaskId;
                _prefetchProfile = GetProfileFromResponse(resp.UpscalerProfile);

                // Start prefetch keep-alive
                _prefetchKeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _prefetchKeepAliveTask = Task.Run(async () =>
                {
                    using var keepScope = serviceScopeFactory.CreateScope();
                    var keepClient = keepScope.ServiceProvider
                        .GetRequiredService<UpscalingService.UpscalingServiceClient>();
                    var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                    while (!_prefetchKeepAliveCts.IsCancellationRequested)
                    {
                        try
                        {
                            if (!_prefetchTaskId.HasValue) break;
                            var ka = await keepClient.KeepAliveAsync(
                                new KeepAliveRequest { TaskId = _prefetchTaskId.Value, Prefetch = true },
                                cancellationToken: _prefetchKeepAliveCts.Token);
                            if (!ka.IsAlive)
                            {
                                _prefetchTaskId = null;
                                break;
                            }
                        }
                        catch { }

                        try { await timer.WaitForNextTickAsync(_prefetchKeepAliveCts.Token); }
                        catch { break; }
                    }
                }, _prefetchKeepAliveCts.Token);

                // Download file
                var sw = Stopwatch.StartNew();
                AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                    new CbzToUpscaleRequest { TaskId = resp.TaskId, Prefetch = true },
                    cancellationToken: stoppingToken);
                string file = await FetchFile(resp.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));
                sw.Stop();
                _downloadTimeStats.Add(sw.Elapsed.TotalSeconds);

                _prefetchFilePath = file;
                _prefetchNextAllowedAt = DateTime.MinValue; // reset backoff on success
                _prefetchNoTaskStreak = 0;

                // Enqueue for upscaling
                var fetched = new FetchedItem(resp.TaskId, _prefetchProfile!, file, _prefetchKeepAliveCts);
                await _toUpscale.Writer.WriteAsync(fetched, stoppingToken);

                // Keep prefetch state until upscaler starts; it will clear and cancel keepalive.
                _fetchInProgress = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                _fetchInProgress = false;
                // Soft failure; wait a bit before next signal consumption
                try { await Task.Delay(500, stoppingToken); }
                catch { break; }
            }
        }
    }

    // Upscale: processes fetched items, streams progress/keep-alives, and triggers next fetch via _fetchSignals
    private async Task UpscaleLoop(CancellationToken stoppingToken)
    {
        if (_toUpscale is null || _toUpload is null) return;

        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();

        while (!stoppingToken.IsCancellationRequested)
        {
            FetchedItem item;
            try { item = await _toUpscale.Reader.ReadAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _currentTaskId = item.TaskId;
            var profile = item.Profile;

            var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            DateTime lastProgressSend = DateTime.UtcNow.AddSeconds(-10);
            int? lastProgressCurrent = null;
            bool signaledThisTask = false;
            DateTime lastProgressTime = DateTime.UtcNow;

            // Start processing keep-alive loop first
            var keepAliveTask = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (!keepAliveCts.IsCancellationRequested)
                {
                    try
                    {
                        var resp = await client.KeepAliveAsync(new KeepAliveRequest { TaskId = item.TaskId },
                            cancellationToken: keepAliveCts.Token);
                        if (!resp.IsAlive)
                        {
                            await keepAliveCts.CancelAsync();
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                    {
                        try { await keepAliveCts.CancelAsync(); }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "KeepAlive loop error for task {taskId}", item.TaskId);
                    }

                    try { await timer.WaitForNextTickAsync(keepAliveCts.Token); }
                    catch { break; }
                }
            }, keepAliveCts.Token);

            // Now cancel prefetch keep-alive and clear prefetch state
            if (item.PrefetchKeepAliveCts is not null)
            {
                try { await item.PrefetchKeepAliveCts.CancelAsync(); }
                catch { }
            }

            _prefetchTaskId = null;
            _prefetchFilePath = null;
            _prefetchProfile = null;

            string upscaledFile = PrepareTempFile(item.TaskId, "_upscaled");
            var progress = new Progress<UpscaleProgress>(async p =>
            {
                DateTime now = DateTime.UtcNow;

                // Update per-page stats and trigger fetch when appropriate
                if (p.Total.HasValue && p.Current.HasValue)
                {
                    int total = p.Total.Value;
                    int current = p.Current.Value;
                    if (lastProgressCurrent.HasValue && current > lastProgressCurrent.Value)
                    {
                        int deltaPages = current - lastProgressCurrent.Value;
                        double deltaTime = (now - lastProgressTime).TotalSeconds;
                        if (deltaPages > 0 && deltaTime > 0)
                        {
                            _perPageTimeStats.Add(deltaTime / deltaPages);
                        }
                    }

                    lastProgressCurrent = current;
                    lastProgressTime = now;

                    int remaining = Math.Max(0, total - current);
                    bool quarterLeft = remaining <= (int)Math.Ceiling(total * 0.25);
                    bool fiveLeft = remaining <= 5;
                    bool etaTrigger = false;
                    if (_downloadTimeStats.Count > 0 && _perPageTimeStats.Count > 0)
                    {
                        double download95 = _downloadTimeStats.P95Upper;
                        double perPage95 = _perPageTimeStats.P95Upper;
                        double remaining95 = remaining * perPage95;
                        etaTrigger = remaining95 <= download95;
                    }

                    if (!signaledThisTask && (quarterLeft || fiveLeft || etaTrigger))
                    {
                        signaledThisTask = true;
                        _fetchSignals?.Writer.TryWrite(true);
                    }
                }

                if (now - lastProgressSend < TimeSpan.FromMilliseconds(400)) return;
                lastProgressSend = now;
                try
                {
                    var req = new KeepAliveRequest { TaskId = item.TaskId };
                    if (p.Total.HasValue) req.Total = p.Total.Value;
                    if (p.Current.HasValue) req.Current = p.Current.Value;
                    if (!string.IsNullOrWhiteSpace(p.StatusMessage)) req.StatusMessage = p.StatusMessage;
                    if (!string.IsNullOrWhiteSpace(p.Phase)) req.Phase = p.Phase;
                    var resp = await client.KeepAliveAsync(req, cancellationToken: stoppingToken);
                    if (!resp.IsAlive)
                    {
                        await keepAliveCts.CancelAsync();
                        return;
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                {
                    try { await keepAliveCts.CancelAsync(); }
                    catch { }

                    return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "KeepAlive progress send failed for task {taskId}", item.TaskId);
                }
            });

            try
            {
                await upscaler.Upscale(item.DownloadedFile, upscaledFile, profile, progress, stoppingToken);
            }
            catch (NotImplementedException)
            {
                await upscaler.Upscale(item.DownloadedFile, upscaledFile, profile, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Cleanup and continue
                if (File.Exists(item.DownloadedFile)) File.Delete(item.DownloadedFile);
                if (File.Exists(upscaledFile)) File.Delete(upscaledFile);
                _currentTaskId = null;
                await keepAliveCts.CancelAsync();
                try { await keepAliveTask; }
                catch { }

                continue;
            }
            catch (Exception ex)
            {
                // Report failure, cleanup, and continue
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest { TaskId = item.TaskId, ErrorMessage = ex.Message },
                        cancellationToken: stoppingToken);
                }
                catch (Exception rpcEx)
                {
                    logger.LogWarning(rpcEx, "Failed to report task failure for {taskId}", item.TaskId);
                }

                if (File.Exists(item.DownloadedFile))
                {
                    try { File.Delete(item.DownloadedFile); }
                    catch { }
                }

                if (File.Exists(upscaledFile))
                {
                    try { File.Delete(upscaledFile); }
                    catch { }
                }

                _currentTaskId = null;
                await keepAliveCts.CancelAsync();
                try { await keepAliveTask; }
                catch { }

                continue;
            }

            await keepAliveCts.CancelAsync();
            try { await keepAliveTask; }
            catch { }

            // Send to upload
            await _toUpload.Writer.WriteAsync(new ProcessedItem(item.TaskId, item.DownloadedFile, upscaledFile),
                stoppingToken);
            _currentTaskId = null;
        }
    }

    // Upload: streams the upscaled CBZ back and deletes temp files
    private async Task UploadLoop(CancellationToken stoppingToken)
    {
        if (_toUpload is null) return;

        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            ProcessedItem item;
            try { item = await _toUpload.Reader.ReadAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _uploadInProgressTaskId = item.TaskId;
            try
            {
                await UploadFileAndCleanup(client, logger, item.TaskId, item.UpscaledFile, item.DownloadedFile,
                    item.UpscaledFile, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service shutting down; ignore
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task {taskId} failed during upload.", item.TaskId);
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest { TaskId = item.TaskId, ErrorMessage = ex.Message },
                        cancellationToken: stoppingToken);
                }
                catch (Exception rpcEx)
                {
                    logger.LogWarning(rpcEx, "Failed to report upload failure for {taskId}", item.TaskId);
                }
            }
            finally
            {
                _uploadInProgressTaskId = null;
            }
        }
    }

    private async Task<Task?> RunCycle(Task? pendingUpload, CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();

        // Ensure we have a prefetched reservation+file ready before processing. This is the only fetch path.
        bool prefetchReady = await EnsurePrefetchReadyAsync(client, logger, stoppingToken);
        if (!prefetchReady)
        {
            // Nothing available to do; light backoff
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch { }

            return null;
        }

        // Use prefetched task if available, otherwise request a new one
        UpscaleTaskDelegationResponse taskResponse;
        bool usingPrefetch = _prefetchTaskId.HasValue && !string.IsNullOrEmpty(_prefetchFilePath) &&
                             _prefetchProfile is not null;
        // Locals to hold consumed prefetch data so we can clear shared fields and allow another prefetch
        int? consumedPrefetchTaskId = null;
        UpscalerProfile? consumedPrefetchProfile = null;
        string? consumedPrefetchFilePath = null;
        if (usingPrefetch)
        {
            // Capture and consume the prefetched state
            consumedPrefetchTaskId = _prefetchTaskId!.Value;
            consumedPrefetchProfile = _prefetchProfile!;
            consumedPrefetchFilePath = _prefetchFilePath!;

            taskResponse = new UpscaleTaskDelegationResponse
            {
                TaskId = consumedPrefetchTaskId.Value,
                UpscalerProfile = new Api.Upscaling.UpscalerProfile
                {
                    Name = consumedPrefetchProfile.Name,
                    Quality = consumedPrefetchProfile.Quality,
                    CompressionFormat = consumedPrefetchProfile.CompressionFormat switch
                    {
                        CompressionFormat.Webp => Api.Upscaling.CompressionFormat.Webp,
                        CompressionFormat.Png => Api.Upscaling.CompressionFormat.Png,
                        CompressionFormat.Jpg => Api.Upscaling.CompressionFormat.Jpg,
                        CompressionFormat.Avif => Api.Upscaling.CompressionFormat.Avif,
                        _ => Api.Upscaling.CompressionFormat.Unspecified
                    },
                    ScalingFactor = consumedPrefetchProfile.ScalingFactor switch
                    {
                        ScaleFactor.OneX => Api.Upscaling.ScaleFactor.OneX,
                        ScaleFactor.TwoX => Api.Upscaling.ScaleFactor.TwoX,
                        ScaleFactor.ThreeX => Api.Upscaling.ScaleFactor.ThreeX,
                        ScaleFactor.FourX => Api.Upscaling.ScaleFactor.FourX,
                        _ => Api.Upscaling.ScaleFactor.Unspecified
                    },
                    UpscalerMethod = consumedPrefetchProfile.UpscalerMethod switch
                    {
                        UpscalerMethod.MangaJaNai => Api.Upscaling.UpscalerMethod.MangaJaNai,
                        _ => Api.Upscaling.UpscalerMethod.Unspecified
                    }
                }
            };
        }
        else
        {
            // Prefetch should always be present here; if not, treat as no work.
            logger.LogDebug("Prefetch not ready despite EnsurePrefetchReady; backing off.");
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch { }

            return null;
        }

        logger.LogInformation("Received task {taskId}.", taskResponse.TaskId);
        _currentTaskId = taskResponse.TaskId;

        var profile = GetProfileFromResponse(taskResponse.UpscalerProfile);

        var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Progress reporting state
        DateTime lastProgressSend = DateTime.UtcNow.AddSeconds(-10);
        int? lastTotal = null;
        int? lastCurrent = null;
        string? lastStatus = null;
        string? lastPhase = null;
        // Prefetch trigger gating is based on field state; no local sticky flag.

        var keepAliveTask = Task.Run(async () =>
        {
            var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    var req = new KeepAliveRequest { TaskId = taskResponse.TaskId };
                    KeepAliveResponse? response =
                        await client.KeepAliveAsync(req, cancellationToken: keepAliveCts.Token);
                    if (!response.IsAlive)
                    {
                        logger.LogWarning(
                            "Keep-alive for task {taskId} failed. Server reports task is no longer alive. Aborting.",
                            taskResponse.TaskId);
                        await keepAliveCts.CancelAsync();
                    }
                }
                catch (Exception ex)
                {
                    if (ex is RpcException rpcEx && rpcEx.StatusCode == StatusCode.NotFound)
                    {
                        // Server no longer recognizes the task; stop keep-alives
                        logger.LogDebug(ex, "KeepAlive loop got NotFound for task {taskId}; stopping.",
                            taskResponse.TaskId);
                        try { await keepAliveCts.CancelAsync(); }
                        catch { }
                    }
                    else
                    {
                        logger.LogError(ex, "Failed to send keep-alive for task {taskId}", taskResponse.TaskId);
                    }
                }

                if (keepAliveCts.IsCancellationRequested)
                {
                    break;
                }

                try { await keepAliveTimer.WaitForNextTickAsync(keepAliveCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, keepAliveCts.Token);

        // Now that processing keep-alive is active, safely clear/cancel prefetch keep-alive to avoid reservation gaps
        if (usingPrefetch)
        {
            _prefetchTaskId = null;
            _prefetchFilePath = null;
            _prefetchProfile = null;
            if (_prefetchKeepAliveCts is not null)
            {
                try { await _prefetchKeepAliveCts.CancelAsync(); }
                catch { }

                _prefetchKeepAliveCts.Dispose();
                _prefetchKeepAliveCts = null;
            }

            _prefetchKeepAliveTask = null;
        }

        // With pipeline-only prefetch, we always have a prefetched file to use
        string downloadedFile = usingPrefetch
            ? consumedPrefetchFilePath!
            : throw new InvalidOperationException("Prefetch not ready");
        string? upscaledFile = null;
        try
        {
            logger.LogInformation("Using prefetched file {file} for task {taskId}.", downloadedFile,
                taskResponse.TaskId);

            upscaledFile = PrepareTempFile(taskResponse.TaskId, "_upscaled");
            // Try to stream progress if the upscaler supports it
            DateTime lastProgressTime = DateTime.UtcNow;
            int? lastProgressCurrent = null;
            // Prefetch is considered in-progress if a reservation exists or a background attempt is running

            var progress = new Progress<UpscaleProgress>(async p =>
            {
                DateTime now = DateTime.UtcNow;

                // Always record latest progress values
                lastTotal = p.Total;
                lastCurrent = p.Current;
                lastStatus = p.StatusMessage;
                lastPhase = p.Phase;

                // Update per-page stats and evaluate prefetch trigger on every progress event
                if (p.Total.HasValue && p.Current.HasValue)
                {
                    int total = p.Total.Value;
                    int current = p.Current.Value;
                    if (lastProgressCurrent.HasValue && current > lastProgressCurrent.Value)
                    {
                        int deltaPages = current - lastProgressCurrent.Value;
                        double deltaTime = (now - lastProgressTime).TotalSeconds;
                        if (deltaPages > 0 && deltaTime > 0)
                        {
                            _perPageTimeStats.Add(deltaTime / deltaPages);
                        }
                    }

                    lastProgressCurrent = current;
                    lastProgressTime = now;

                    // Only attempt prefetch if none is running/reserved right now
                    bool prefetchInProgress = _prefetchTask is not null || _prefetchTaskId.HasValue;
                    if (!prefetchInProgress)
                    {
                        int remaining = Math.Max(0, total - current);
                        bool near75 = current >= (int)Math.Ceiling(total * 0.75);
                        bool fiveLeft = remaining <= 5;
                        bool etaTrigger = false;
                        if (_downloadTimeStats.Count > 0 && _perPageTimeStats.Count > 0)
                        {
                            double download95 = _downloadTimeStats.P95Upper;
                            double perPage95 = _perPageTimeStats.P95Upper;
                            double remaining95 = remaining * perPage95;
                            etaTrigger = remaining95 <= download95;
                            if (etaTrigger)
                            {
                                logger.LogDebug(
                                    "Prefetch ETA trigger: remaining={remaining}, perPage95={perPage95:F2}s, download95={download95:F2}s",
                                    remaining, perPage95, download95);
                            }
                        }

                        if (near75 || fiveLeft || etaTrigger)
                        {
                            if (DateTime.UtcNow >= _prefetchNextAllowedAt)
                            {
                                _prefetchTask = TryStartPrefetchAsync(client, logger, stoppingToken);
                            }
                            else
                            {
                                logger.LogDebug("Prefetch suppressed due to backoff until {until:u}",
                                    _prefetchNextAllowedAt);
                            }
                        }
                    }
                }

                // Debounced keep-alive send (for bandwidth)
                if (now - lastProgressSend < TimeSpan.FromMilliseconds(400))
                {
                    return;
                }

                lastProgressSend = now;
                try
                {
                    var req = new KeepAliveRequest { TaskId = taskResponse.TaskId };
                    if (p.Total.HasValue)
                    {
                        req.Total = p.Total.Value;
                    }

                    if (p.Current.HasValue)
                    {
                        req.Current = p.Current.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                    {
                        req.StatusMessage = p.StatusMessage;
                    }

                    if (!string.IsNullOrWhiteSpace(p.Phase))
                    {
                        req.Phase = p.Phase;
                    }

                    KeepAliveResponse? resp = await client.KeepAliveAsync(req, cancellationToken: stoppingToken);
                    if (!resp.IsAlive)
                    {
                        // Server canceled the task; cancel local processing loop
                        logger.LogWarning(
                            "Server reported task {taskId} not alive during progress; cancelling locally.",
                            taskResponse.TaskId);
                        await keepAliveCts.CancelAsync();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (ex is RpcException rpcEx && rpcEx.StatusCode == StatusCode.NotFound)
                    {
                        logger.LogDebug(ex, "KeepAlive progress got NotFound for task {taskId}; stopping.",
                            taskResponse.TaskId);
                        try { await keepAliveCts.CancelAsync(); }
                        catch { }

                        return;
                    }
                    else
                    {
                        logger.LogDebug(ex, "KeepAlive progress send failed for task {taskId}", taskResponse.TaskId);
                    }
                }
            });

            try
            {
                await upscaler.Upscale(downloadedFile, upscaledFile, profile, progress, stoppingToken);
            }
            catch (NotImplementedException)
            {
                // Fallback: no progress reporting
                await upscaler.Upscale(downloadedFile, upscaledFile, profile, stoppingToken);
            }

            logger.LogInformation("Upscaled file {file} for task {taskId}.", upscaledFile, taskResponse.TaskId);
            logger.LogDebug("Stats summary: download P95≈{d95:F2}s (n={dn}), per-page P95≈{p95:F2}s (n={pn})",
                _downloadTimeStats.P95Upper, _downloadTimeStats.Count, _perPageTimeStats.P95Upper,
                _perPageTimeStats.Count);

            if (pendingUpload != null)
            {
                await pendingUpload;
            }

            var uploadTask = UploadFileAndCleanup(client, logger, taskResponse.TaskId, upscaledFile, downloadedFile,
                upscaledFile, stoppingToken);
            // Track upload-in-progress to avoid prefetch of the same task while uploading
            _uploadInProgressTaskId = taskResponse.TaskId;
            _ = uploadTask.ContinueWith(_ => _uploadInProgressTaskId = null, TaskScheduler.Default);
            return uploadTask;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {taskId} was cancelled.", taskResponse.TaskId);
            if (downloadedFile != null && File.Exists(downloadedFile))
            {
                File.Delete(downloadedFile);
            }

            if (upscaledFile != null && File.Exists(upscaledFile))
            {
                File.Delete(upscaledFile);
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process task {taskId}.", taskResponse.TaskId);
            await client.ReportTaskFailedAsync(new ReportTaskFailedRequest
            {
                TaskId = taskResponse.TaskId, ErrorMessage = ex.Message
            });
            if (downloadedFile != null && File.Exists(downloadedFile))
            {
                File.Delete(downloadedFile);
            }

            if (upscaledFile != null && File.Exists(upscaledFile))
            {
                File.Delete(upscaledFile);
            }

            return null;
        }
        finally
        {
            await keepAliveCts.CancelAsync();
            try { await keepAliveTask; }
            catch (OperationCanceledException) { }

            _currentTaskId = null;

            // If we consumed a prefetched task, clear and stop its keepalive
            if (usingPrefetch)
            {
                _prefetchTaskId = null;
                _prefetchFilePath = null;
                _prefetchProfile = null;
                if (_prefetchKeepAliveCts is not null)
                {
                    try { await _prefetchKeepAliveCts.CancelAsync(); }
                    catch { }

                    _prefetchKeepAliveCts.Dispose();
                    _prefetchKeepAliveCts = null;
                }

                _prefetchKeepAliveTask = null;
            }
        }
    }

    private async Task WaitForPrefetchIfInProgressAsync(ILogger<RemoteTaskProcessor> logger,
        CancellationToken stoppingToken)
    {
        try
        {
            // Snapshot the current prefetch task handle (method will clear the field on completion)
            Task? taskSnapshot = _prefetchTask;
            logger.LogInformation("Waiting for prefetched task {taskId} to finish download to maintain pipeline...",
                _prefetchTaskId);

            while (_prefetchTaskId.HasValue && string.IsNullOrEmpty(_prefetchFilePath) &&
                   !stoppingToken.IsCancellationRequested)
            {
                if (taskSnapshot != null)
                {
                    await Task.WhenAny(taskSnapshot, Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken));
                    // Refresh snapshot if the field changed (e.g., restarted)
                    if (!ReferenceEquals(taskSnapshot, _prefetchTask))
                    {
                        taskSnapshot = _prefetchTask;
                    }
                }
                else
                {
                    // No task handle available; just poll briefly for file readiness
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                }
            }

            if (!string.IsNullOrEmpty(_prefetchFilePath))
            {
                logger.LogDebug("Prefetch finished before next cycle; proceeding with prefetched task {taskId}.",
                    _prefetchTaskId);
            }
            else
            {
                logger.LogDebug("Prefetch unavailable (reservation cleared or cancellation). Proceeding without it.");
            }
        }
        catch (OperationCanceledException)
        {
            // Respect cancellation; upper layer will handle
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while waiting for prefetch; proceeding without it.");
        }
    }

    private async Task TryStartPrefetchAsync(UpscalingService.UpscalingServiceClient _ignoredClient,
        ILogger<RemoteTaskProcessor> logger, CancellationToken stoppingToken)
    {
        if (_prefetchTaskId.HasValue || _prefetchTask is not null)
        {
            return; // already prefetched/in progress
        }

        try
        {
            using IServiceScope scope = serviceScopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
            // Loop until we acquire a reservation for a different task than current/uploading
            UpscaleTaskDelegationResponse? resp;
            while (true)
            {
                resp = await client.RequestUpscaleTaskWithHintAsync(new RequestTaskRequest { Prefetch = true },
                    cancellationToken: stoppingToken);
                if (resp.TaskId == -1)
                {
                    // nothing available to prefetch
                    return;
                }

                // Avoid prefetching the task currently being processed or uploaded
                if ((_currentTaskId.HasValue && resp.TaskId == _currentTaskId.Value) ||
                    (_uploadInProgressTaskId.HasValue && resp.TaskId == _uploadInProgressTaskId.Value))
                {
                    logger.LogDebug("Prefetch received in-flight task {taskId}; retrying shortly", resp.TaskId);
                    try { await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken); }
                    catch { }

                    continue;
                }

                break;
            }

            _prefetchTaskId = resp.TaskId;
            _prefetchProfile = GetProfileFromResponse(resp.UpscalerProfile);

            // Start keepalive for prefetched task
            _prefetchKeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _prefetchKeepAliveTask = Task.Run(async () =>
            {
                using IServiceScope keepScope = serviceScopeFactory.CreateScope();
                var keepClient =
                    keepScope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
                var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (!_prefetchKeepAliveCts.IsCancellationRequested)
                {
                    try
                    {
                        if (!_prefetchTaskId.HasValue)
                        {
                            break;
                        }

                        var respKA = await keepClient.KeepAliveAsync(
                            new KeepAliveRequest { TaskId = _prefetchTaskId.Value, Prefetch = true },
                            cancellationToken: _prefetchKeepAliveCts.Token);
                        if (!respKA.IsAlive)
                        {
                            // Prefetch reservation cancelled on server
                            _prefetchTaskId = null;
                            _prefetchFilePath = null;
                            _prefetchProfile = null;
                            try { await _prefetchKeepAliveCts.CancelAsync(); }
                            catch { }

                            break;
                        }
                    }
                    catch { }

                    try { await keepAliveTimer.WaitForNextTickAsync(_prefetchKeepAliveCts.Token); }
                    catch { break; }
                }
            }, _prefetchKeepAliveCts.Token);

            // Download the CBZ for the prefetched task
            var sw = Stopwatch.StartNew();
            AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                new CbzToUpscaleRequest { TaskId = resp.TaskId, Prefetch = true }, cancellationToken: stoppingToken);
            string file = await FetchFile(resp.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));
            sw.Stop();
            _downloadTimeStats.Add(sw.Elapsed.TotalSeconds);
            logger.LogDebug("Prefetch download sample: {sec:F2}s; P95≈{p95:F2}s (n={n})", sw.Elapsed.TotalSeconds,
                _downloadTimeStats.P95Upper, _downloadTimeStats.Count);
            _prefetchFilePath = file;
            logger.LogInformation("Prefetched task {taskId} in {sec:F2}s.", resp.TaskId, sw.Elapsed.TotalSeconds);
            // Reset prefetch backoff on success
            _prefetchNoTaskStreak = 0;
            _prefetchNextAllowedAt = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            if (ex is RpcException rpcEx && rpcEx.StatusCode == StatusCode.NotFound)
            {
                // Server indicated no work via RPC status; treat like -1
                _prefetchNoTaskStreak++;
                int delaySec = Math.Min(30, 5 * _prefetchNoTaskStreak);
                _prefetchNextAllowedAt = DateTime.UtcNow.AddSeconds(delaySec);
                logger.LogDebug("Prefetch: no task available (RPC), retry in {sec}s (streak={streak})", delaySec,
                    _prefetchNoTaskStreak);
            }

            // Reset prefetch state on failure
            _prefetchTaskId = null;
            _prefetchFilePath = null;
            _prefetchProfile = null;
            if (_prefetchKeepAliveCts is not null)
            {
                try { await _prefetchKeepAliveCts.CancelAsync(); }
                catch { }

                _prefetchKeepAliveCts.Dispose();
                _prefetchKeepAliveCts = null;
            }

            _prefetchKeepAliveTask = null;
            logger.LogDebug(ex, "Prefetch failed");
        }
        finally
        {
            _prefetchTask = null;
        }
    }

    // Ensure we have a reservation and downloaded file ready.
    private async Task<bool> EnsurePrefetchReadyAsync(UpscalingService.UpscalingServiceClient client,
        ILogger<RemoteTaskProcessor> logger, CancellationToken stoppingToken)
    {
        // If a prefetch reservation exists but the download hasn't finished yet, wait for it
        if (_prefetchTaskId.HasValue && string.IsNullOrEmpty(_prefetchFilePath))
        {
            await WaitForPrefetchIfInProgressAsync(logger, stoppingToken);
        }

        // If we already have a ready file, we're good
        if (_prefetchTaskId.HasValue && !string.IsNullOrEmpty(_prefetchFilePath) && _prefetchProfile is not null)
        {
            return true;
        }

        // Otherwise, start prefetch and wait for it to complete or conclude no work
        if (_prefetchTask is null)
        {
            _prefetchTask = TryStartPrefetchAsync(client, logger, stoppingToken);
        }

        // Wait briefly until either a file appears or we determine there's no task (-1 path yields no reservation)
        var start = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_prefetchTaskId.HasValue && !string.IsNullOrEmpty(_prefetchFilePath) && _prefetchProfile is not null)
            {
                return true;
            }

            // If no reservation and background attempt finished, give up for now
            if (_prefetchTask is null)
            {
                break;
            }

            // Wait a bit and re-check
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken); }
            catch { break; }
        }

        return false;
    }

    private async Task UploadFile(UpscalingService.UpscalingServiceClient client, ILogger<RemoteTaskProcessor> logger,
        int taskId, string upscaledFile, CancellationToken stoppingToken)
    {
        await using FileStream fileStream = File.OpenRead(upscaledFile);
        AsyncDuplexStreamingCall<CbzFileChunk, UploadUpscaledCbzResponse>? uploadStream =
            client.UploadUpscaledCbzFile(cancellationToken: stoppingToken);
        byte[] buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, stoppingToken)) > 0)
        {
            await uploadStream.RequestStream.WriteAsync(
                new CbzFileChunk
                {
                    TaskId = taskId, ChunkNumber = chunkNumber++, Chunk = ByteString.CopyFrom(buffer, 0, bytesRead)
                }, stoppingToken);
        }

        await uploadStream.RequestStream.CompleteAsync();

        await foreach (UploadUpscaledCbzResponse response in uploadStream.ResponseStream.ReadAllAsync(stoppingToken))
        {
            if (response.Success)
            {
                logger.LogInformation("Successfully uploaded upscaled file for task {taskId}.", response.TaskId);
            }
            else
            {
                logger.LogError("Failed to upload upscaled file for task {taskId}: {message}", response.TaskId,
                    response.Message);
            }
        }
    }

    private async Task UploadFileAndCleanup(UpscalingService.UpscalingServiceClient client,
        ILogger<RemoteTaskProcessor> logger, int taskId, string upscaledFile, string? downloadedFile,
        string? upscaledFileForCleanup, CancellationToken stoppingToken)
    {
        try
        {
            await UploadFile(client, logger, taskId, upscaledFile, stoppingToken);
        }
        finally
        {
            if (downloadedFile != null && File.Exists(downloadedFile))
            {
                File.Delete(downloadedFile);
            }

            if (upscaledFileForCleanup != null && File.Exists(upscaledFileForCleanup))
                File.Delete(upscaledFileForCleanup);
        }
    }

    private async Task<string> FetchFile(int taskId, IAsyncEnumerable<CbzFileChunk> stream)
    {
        Dictionary<int, byte[]> chunks = new();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk.ChunkNumber, chunk.Chunk.ToByteArray());
        }

        var tempFile = PrepareTempFile(taskId);
        await using (FileStream output = File.OpenWrite(tempFile))
        {
            foreach (var chunk in chunks.OrderBy(c => c.Key))
            {
                await output.WriteAsync(chunk.Value.AsMemory(0, chunk.Value.Length));
            }
        }

        return tempFile;
    }

    private string PrepareTempFile(int taskId, string? suffix = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling", "remoteworker");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"task_{taskId}{suffix ?? ""}.cbz");
    }

    private static UpscalerProfile GetProfileFromResponse(Api.Upscaling.UpscalerProfile upscalerProfile)
    {
        return new UpscalerProfile
        {
            CompressionFormat = upscalerProfile.CompressionFormat switch
            {
                Api.Upscaling.CompressionFormat.Webp => CompressionFormat.Webp,
                Api.Upscaling.CompressionFormat.Png => CompressionFormat.Png,
                Api.Upscaling.CompressionFormat.Jpg => CompressionFormat.Jpg,
                Api.Upscaling.CompressionFormat.Avif => CompressionFormat.Avif,
                _ => throw new InvalidOperationException("Unknown compression format.")
            },
            Name = upscalerProfile.Name,
            Quality = upscalerProfile.Quality,
            ScalingFactor = upscalerProfile.ScalingFactor switch
            {
                Api.Upscaling.ScaleFactor.OneX => ScaleFactor.OneX,
                Api.Upscaling.ScaleFactor.TwoX => ScaleFactor.TwoX,
                Api.Upscaling.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                Api.Upscaling.ScaleFactor.FourX => ScaleFactor.FourX,
                _ => throw new InvalidOperationException("Unknown scaling factor.")
            },
            UpscalerMethod = upscalerProfile.UpscalerMethod switch
            {
                Api.Upscaling.UpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                _ => throw new InvalidOperationException("Unknown upscaler method.")
            }
        };
    }

    private sealed record FetchedItem(
        int TaskId,
        UpscalerProfile Profile,
        string DownloadedFile,
        CancellationTokenSource? PrefetchKeepAliveCts);

    private sealed record ProcessedItem(int TaskId, string DownloadedFile, string UpscaledFile);

    // Online statistics (Welford's method) to estimate download and per-page processing times
    private sealed class OnlineStats
    {
        public int Count { get; private set; }
        public double Mean { get; private set; }
        public double M2 { get; private set; }

        public double StdDev => Count > 1 ? Math.Sqrt(M2 / (Count - 1)) : 0.0;
        public double P95Upper => Mean + (1.96 * StdDev);

        public void Add(double x)
        {
            Count++;
            double delta = x - Mean;
            Mean += delta / Count;
            double delta2 = x - Mean;
            M2 += delta * delta2;
        }
    }
}