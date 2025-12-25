using System.Diagnostics;
using System.IO.Compression;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using CompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using ScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerMethod = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    // Tracks download and processing statistics to optimize prefetch timing.
    private readonly PrefetchPredictor _predictor = new();

    // State tracking for task lifecycle coordination and exclusion.
    private int? _currentTaskId;
    private volatile bool _fetchInProgress;

    // Channel-based processing pipeline for coordinating fetch, upscale, and upload operations.
    private Channel<bool>? _fetchSignals;
    private Channel<ProcessedItem>? _toUpload;
    private Channel<FetchedItem>? _toUpscale;
    private int? _uploadInProgressTaskId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        logger.LogInformation("Successfully connected to server and waiting for work.");

        _toUpscale = Channel.CreateBounded<FetchedItem>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        _toUpload = Channel.CreateBounded<ProcessedItem>(
            new BoundedChannelOptions(3)
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
        Task upscaleTask = UpscaleLoop(stoppingToken);
        Task uploadTask = UploadLoop(stoppingToken);

        await Task.WhenAll(fetchTask, upscaleTask, uploadTask);
    }

    /// <summary>
    ///     Handles task reservation, file downloading, and coordination with the upscale pipeline.
    ///     Waits for fetch signals, reserves tasks with prefetch hints, downloads CBZ files,
    ///     and maintains task reservations until handed off to the upscaler.
    /// </summary>
    private async Task FetchLoop(CancellationToken stoppingToken)
    {
        var dispatcherTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        bool serverAvailable = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_fetchSignals is null || _toUpscale is null)
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

                if (!await _toUpscale.Writer.WaitToWriteAsync(stoppingToken))
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

                    if (
                        (_currentTaskId.HasValue && resp.TaskId == _currentTaskId.Value)
                        || (
                            _uploadInProgressTaskId.HasValue
                            && resp.TaskId == _uploadInProgressTaskId.Value
                        )
                    )
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

                int prefetchTaskId = resp.TaskId;
                logger.LogInformation(
                    "Received task {TaskId} of type {TaskType} from server.",
                    prefetchTaskId,
                    resp.TaskType
                );
                UpscalerProfile prefetchProfile = GetProfileFromResponse(resp.UpscalerProfile);

                var persistentKeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken
                );
                Task persistentKeepAliveTask = RunKeepAliveLoop(
                    persistentKeepAliveCts,
                    () => prefetchTaskId,
                    id => new KeepAliveRequest { TaskId = id, Prefetch = true }
                );

                var sw = Stopwatch.StartNew();
                AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                    new CbzToUpscaleRequest { TaskId = resp.TaskId, Prefetch = true },
                    cancellationToken: stoppingToken
                );
                string file = await FetchFile(
                    resp.TaskId,
                    stream.ResponseStream.ReadAllAsync(stoppingToken)
                );
                sw.Stop();
                _predictor.RecordDownload(sw.Elapsed);

                var fetched = new FetchedItem(
                    resp.TaskId,
                    prefetchProfile,
                    file,
                    persistentKeepAliveCts,
                    persistentKeepAliveTask,
                    resp.TaskType,
                    resp.SplitFindingsJson
                );
                await _toUpscale.Writer.WriteAsync(fetched, stoppingToken);

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

    /// <summary>
    ///     Processes fetched items through the upscaling pipeline.
    ///     Handles progress reporting, keep-alive management, and triggers the next fetch operation
    ///     using predictive prefetch algorithms.
    /// </summary>
    private async Task UpscaleLoop(CancellationToken stoppingToken)
    {
        if (_toUpscale is null || _toUpload is null)
            return;

        using var scope = serviceScopeFactory.CreateScope();
        var client =
            scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();
        var splitDetectionService =
            scope.ServiceProvider.GetRequiredService<ISplitDetectionService>();
        var splitApplier = scope.ServiceProvider.GetRequiredService<ISplitApplier>();

        while (!stoppingToken.IsCancellationRequested)
        {
            FetchedItem item;
            try
            {
                item = await _toUpscale.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _currentTaskId = item.TaskId;
            var profile = item.Profile;

            // Transition the persistent keep-alive from prefetch to processing mode
            // We'll create a new keep-alive specifically for progress reporting during upscaling
            var upscalesCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            DateTime lastProgressSend = DateTime.UtcNow.AddSeconds(-10);
            int? lastProgressCurrent = null;
            int prefetchSignaled = 0; // 0 = not signaled, 1 = signaled (atomic)
            DateTime lastProgressTime = DateTime.UtcNow;

            // The persistent keep-alive continues running; we just add progress reporting
            // No need to start a separate processing keep-alive loop since the persistent one continues

            string? upscaledFile = null;
            string? resultJson = null;
            string? tempExtractDir = null;

            // Progress reporting must not block upscaling: use a bounded channel and a background sender
            var progressChannel = Channel.CreateBounded<UpscaleProgress>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                }
            );

            // Writer: never await network; buffer latest only
            var progress = new Progress<UpscaleProgress>(p =>
            {
                progressChannel.Writer.TryWrite(p);
            });

            // Reader/sender: debounced network I/O off the critical path
            var progressSenderTask = Task.Run(
                async () =>
                {
                    var debounce = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                    UpscaleProgress? pending = null;
                    while (!item.PersistentKeepAliveCts.IsCancellationRequested)
                    {
                        try
                        {
                            // Drain to latest
                            while (progressChannel.Reader.TryRead(out UpscaleProgress? p))
                            {
                                pending = p;
                                DateTime now = DateTime.UtcNow;

                                // Update per-page stats and predictive trigger
                                if (p.Total.HasValue && p.Current.HasValue)
                                {
                                    int total = p.Total.Value;
                                    int current = p.Current.Value;
                                    if (
                                        lastProgressCurrent.HasValue
                                        && current > lastProgressCurrent.Value
                                    )
                                    {
                                        int deltaPages = current - lastProgressCurrent.Value;
                                        double deltaTime = (now - lastProgressTime).TotalSeconds;
                                        if (deltaPages > 0 && deltaTime > 0)
                                        {
                                            _predictor.RecordPerPage(deltaTime / deltaPages);
                                        }
                                    }

                                    lastProgressCurrent = current;
                                    lastProgressTime = now;

                                    int remaining = Math.Max(0, total - current);
                                    bool shouldPrefetch = _predictor.ShouldPrefetch(
                                        remaining,
                                        total
                                    );
                                    if (
                                        shouldPrefetch
                                        && Interlocked.CompareExchange(ref prefetchSignaled, 1, 0)
                                            == 0
                                    )
                                    {
                                        _fetchSignals?.Writer.TryWrite(true);
                                    }
                                }
                            }

                            // Exit if channel is completed and no more data is available
                            if (progressChannel.Reader.Completion.IsCompleted)
                            {
                                break;
                            }

                            // Debounced send of latest progress
                            DateTime nowSend = DateTime.UtcNow;
                            if (
                                pending is not null
                                && nowSend - lastProgressSend >= TimeSpan.FromMilliseconds(400)
                            )
                            {
                                lastProgressSend = nowSend;
                                try
                                {
                                    var req = new KeepAliveRequest { TaskId = item.TaskId };
                                    if (pending.Total.HasValue)
                                    {
                                        req.Total = pending.Total.Value;
                                    }

                                    if (pending.Current.HasValue)
                                    {
                                        req.Current = pending.Current.Value;
                                    }

                                    if (!string.IsNullOrWhiteSpace(pending.StatusMessage))
                                    {
                                        req.StatusMessage = pending.StatusMessage;
                                    }

                                    if (!string.IsNullOrWhiteSpace(pending.Phase))
                                    {
                                        req.Phase = pending.Phase;
                                    }

                                    KeepAliveResponse? resp = await client.KeepAliveAsync(
                                        req,
                                        cancellationToken: upscalesCts.Token
                                    );
                                    if (!resp.IsAlive)
                                    {
                                        await Task.WhenAll(
                                            upscalesCts.CancelAsync(),
                                            item.PersistentKeepAliveCts.CancelAsync()
                                        );
                                        break;
                                    }
                                }
                                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                                {
                                    try
                                    {
                                        await item.PersistentKeepAliveCts.CancelAsync();
                                    }
                                    catch { }

                                    break;
                                }
                                catch (Exception ex)
                                {
                                    logger.LogDebug(
                                        ex,
                                        "KeepAlive progress send failed for task {taskId}",
                                        item.TaskId
                                    );
                                }
                            }

                            try
                            {
                                await debounce.WaitForNextTickAsync(upscalesCts.Token);
                            }
                            catch
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // swallow and continue
                        }
                    }
                },
                upscalesCts.Token
            );

            try
            {
                logger.LogInformation(
                    "Starting processing for task {TaskId} ({TaskType})",
                    item.TaskId,
                    item.TaskType
                );

                if (item.TaskType == TaskType.SplitDetection)
                {
                    logger.LogInformation(
                        "Executing SplitDetection task {TaskId} on Remote Worker",
                        item.TaskId
                    );
                    tempExtractDir = Path.Combine(
                        Path.GetTempPath(),
                        $"mangaingest_worker_extract_{item.TaskId}_{Guid.NewGuid()}"
                    );
                    Directory.CreateDirectory(tempExtractDir);
                    ZipFile.ExtractToDirectory(item.DownloadedFile, tempExtractDir);

                    var results = await splitDetectionService.DetectSplitsAsync(
                        tempExtractDir,
                        progress,
                        upscalesCts.Token
                    );
                    resultJson = System.Text.Json.JsonSerializer.Serialize(
                        results,
                        WorkerJsonContext.Default.ListSplitDetectionResult
                    );
                }
                else if (item.TaskType == TaskType.ApplySplits)
                {
                    tempExtractDir = Path.Combine(
                        Path.GetTempPath(),
                        $"mangaingest_worker_extract_{item.TaskId}_{Guid.NewGuid()}"
                    );
                    var newDir = Path.Combine(
                        Path.GetTempPath(),
                        $"mangaingest_worker_repack_{item.TaskId}_{Guid.NewGuid()}"
                    );
                    Directory.CreateDirectory(tempExtractDir);
                    Directory.CreateDirectory(newDir);

                    ZipFile.ExtractToDirectory(item.DownloadedFile, tempExtractDir);

                    var findings = System.Text.Json.JsonSerializer.Deserialize(
                        item.SplitFindingsJson!,
                        WorkerJsonContext.Default.ListSplitFindingDto
                    );

                    List<SplitDetectionResult> detectionResults = new();
                    bool hasValidFindings = false;

                    if (findings != null && findings.Count > 0)
                    {
                        // Check if findings contain errors by deserializing and inspecting the result
                        if (
                            findings.Any(f =>
                            {
                                var result = System.Text.Json.JsonSerializer.Deserialize(
                                    f.SplitJson,
                                    WorkerJsonContext.Default.SplitDetectionResult
                                );
                                return result != null && !string.IsNullOrEmpty(result.Error);
                            })
                        )
                        {
                            logger.LogDebug(
                                "Task {TaskId}: Findings contain errors. Will re-run detection locally.",
                                item.TaskId
                            );
                        }
                        else
                        {
                            hasValidFindings = true;
                            foreach (var finding in findings)
                            {
                                var result = System.Text.Json.JsonSerializer.Deserialize(
                                    finding.SplitJson,
                                    WorkerJsonContext.Default.SplitDetectionResult
                                );
                                if (result != null)
                                {
                                    // Ensure image path matches what we expect locally if needed,
                                    // but usually we match by filename anyway.
                                    detectionResults.Add(result);
                                }
                            }
                        }
                    }

                    if (!hasValidFindings)
                    {
                        logger.LogDebug(
                            "Task {TaskId}: Running split detection locally on worker.",
                            item.TaskId
                        );
                        detectionResults = await splitDetectionService.DetectSplitsAsync(
                            tempExtractDir,
                            progress,
                            upscalesCts.Token
                        );
                    }

                    var images = Directory
                        .GetFiles(tempExtractDir)
                        .Where(f =>
                            ImageConstants.SupportedImageExtensions.Contains(Path.GetExtension(f))
                        )
                        .ToList();

                    logger.LogInformation(
                        "Task {TaskId}: Found {Count} images in archive.",
                        item.TaskId,
                        images.Count
                    );

                    int appliedCount = 0;
                    foreach (var imagePath in images)
                    {
                        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
                        // Match by filename
                        var result = detectionResults.FirstOrDefault(r =>
                            Path.GetFileNameWithoutExtension(r.ImagePath)
                                .Equals(fileNameWithoutExt, StringComparison.OrdinalIgnoreCase)
                        );

                        if (result != null && result.Splits.Count > 0)
                        {
                            splitApplier.ApplySplitsToImage(imagePath, result.Splits, newDir);
                            appliedCount++;
                        }
                        else
                        {
                            // Copy unsplit
                            var dest = Path.Combine(newDir, Path.GetFileName(imagePath));
                            File.Copy(imagePath, dest);
                        }
                    }

                    logger.LogInformation(
                        "Task {TaskId}: Applied splits to {Count} images.",
                        item.TaskId,
                        appliedCount
                    );

                    // Copy ComicInfo.xml if exists
                    var comicInfo = Path.Combine(tempExtractDir, "ComicInfo.xml");
                    if (File.Exists(comicInfo))
                    {
                        File.Copy(comicInfo, Path.Combine(newDir, "ComicInfo.xml"));
                    }

                    upscaledFile = PrepareTempFile(item.TaskId, "_split.cbz");
                    ZipFile.CreateFromDirectory(newDir, upscaledFile);

                    Directory.Delete(newDir, true);
                }
                else
                {
                    upscaledFile = PrepareTempFile(item.TaskId, "_upscaled");
                    try
                    {
                        await upscaler.Upscale(
                            item.DownloadedFile,
                            upscaledFile,
                            profile,
                            progress,
                            upscalesCts.Token
                        );
                    }
                    catch (NotImplementedException)
                    {
                        await upscaler.Upscale(
                            item.DownloadedFile,
                            upscaledFile,
                            profile,
                            upscalesCts.Token
                        );
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SafeDelete(item.DownloadedFile);
                if (upscaledFile != null)
                    SafeDelete(upscaledFile);
                if (tempExtractDir != null && Directory.Exists(tempExtractDir))
                    Directory.Delete(tempExtractDir, true);
                _currentTaskId = null;
                await item.PersistentKeepAliveCts.CancelAsync();
                try
                {
                    await item.PersistentKeepAliveTask;
                }
                catch { }

                if (Interlocked.CompareExchange(ref prefetchSignaled, 1, 0) == 0)
                {
                    _fetchSignals?.Writer.TryWrite(true);
                }

                continue;
            }
            catch (Exception ex)
            {
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest
                        {
                            TaskId = item.TaskId,
                            ErrorMessage = ex.Message,
                        },
                        cancellationToken: stoppingToken
                    );
                }
                catch (Exception rpcEx)
                {
                    logger.LogWarning(
                        rpcEx,
                        "Failed to report task failure for {taskId}",
                        item.TaskId
                    );
                }

                SafeDelete(item.DownloadedFile);
                if (upscaledFile != null)
                    SafeDelete(upscaledFile);
                if (tempExtractDir != null && Directory.Exists(tempExtractDir))
                    Directory.Delete(tempExtractDir, true);

                _currentTaskId = null;
                await item.PersistentKeepAliveCts.CancelAsync();
                try
                {
                    await item.PersistentKeepAliveTask;
                }
                catch { }

                if (Interlocked.CompareExchange(ref prefetchSignaled, 1, 0) == 0)
                {
                    _fetchSignals?.Writer.TryWrite(true);
                }

                continue;
            }

            try
            {
                progressChannel.Writer.TryComplete();
            }
            catch { }

            try
            {
                await upscalesCts.CancelAsync();
            }
            catch { }

            try
            {
                await progressSenderTask;
            }
            catch { }

            if (Interlocked.CompareExchange(ref prefetchSignaled, 1, 0) == 0)
            {
                _fetchSignals?.Writer.TryWrite(true);
            }

            // Cleanup temp dir if it exists (should be done in finally usually, but here we are at end of loop)
            if (tempExtractDir != null && Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, true);

            await _toUpload.Writer.WriteAsync(
                new ProcessedItem(
                    item.TaskId,
                    item.DownloadedFile,
                    upscaledFile,
                    resultJson,
                    item.PersistentKeepAliveCts,
                    item.PersistentKeepAliveTask,
                    item.TaskType
                ),
                stoppingToken
            );
            _currentTaskId = null;
        }
    }

    /// <summary>
    ///     Handles uploading of processed files back to the server and cleanup of temporary files.
    ///     Maintains keep-alive connections during the upload process.
    /// </summary>
    private async Task UploadLoop(CancellationToken stoppingToken)
    {
        if (_toUpload is null)
            return;

        using var scope = serviceScopeFactory.CreateScope();
        var client =
            scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            ProcessedItem item;
            try
            {
                item = await _toUpload.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _uploadInProgressTaskId = item.TaskId;

            try
            {
                if (item.TaskType == TaskType.SplitDetection)
                {
                    if (item.ResultJson == null)
                        throw new InvalidOperationException(
                            "ResultJson is null for detection task"
                        );
                    await UploadDetectionResultAndCleanup(
                        client,
                        logger,
                        item.TaskId,
                        item.ResultJson,
                        item.DownloadedFile,
                        stoppingToken
                    );
                }
                else
                {
                    if (item.UpscaledFile == null)
                        throw new InvalidOperationException(
                            "UpscaledFile is null for upscale task"
                        );
                    await UploadFileAndCleanup(
                        client,
                        logger,
                        item.TaskId,
                        item.UpscaledFile,
                        item.DownloadedFile,
                        item.UpscaledFile,
                        stoppingToken
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // this happens by normal user interruption (i.e. ctrl+c, stopping the container etc.)
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task {taskId} failed during upload.", item.TaskId);
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest
                        {
                            TaskId = item.TaskId,
                            ErrorMessage = ex.Message,
                        },
                        cancellationToken: stoppingToken
                    );
                }
                catch (Exception rpcEx)
                {
                    logger.LogWarning(
                        rpcEx,
                        "Failed to report upload failure for {taskId}",
                        item.TaskId
                    );
                }
            }
            finally
            {
                await item.PersistentKeepAliveCts.CancelAsync();
                try
                {
                    await item.PersistentKeepAliveTask;
                }
                catch { }

                _uploadInProgressTaskId = null;
            }
        }
    }

    private async Task UploadFile(
        UpscalingService.UpscalingServiceClient client,
        ILogger<RemoteTaskProcessor> logger,
        int taskId,
        string upscaledFile,
        CancellationToken stoppingToken
    )
    {
        await using FileStream fileStream = File.OpenRead(upscaledFile);
        AsyncDuplexStreamingCall<CbzFileChunk, UploadUpscaledCbzResponse>? uploadStream =
            client.UploadUpscaledCbzFile(cancellationToken: stoppingToken);
        byte[] buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        while (
            (bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, stoppingToken)) > 0
        )
        {
            await uploadStream.RequestStream.WriteAsync(
                new CbzFileChunk
                {
                    TaskId = taskId,
                    ChunkNumber = chunkNumber++,
                    Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                },
                stoppingToken
            );
        }

        await uploadStream.RequestStream.CompleteAsync();

        await foreach (
            UploadUpscaledCbzResponse response in uploadStream.ResponseStream.ReadAllAsync(
                stoppingToken
            )
        )
        {
            if (response.Success)
            {
                logger.LogInformation(
                    "Successfully uploaded upscaled file for task {taskId}.",
                    response.TaskId
                );
            }
            else
            {
                logger.LogError(
                    "Failed to upload upscaled file for task {taskId}: {message}",
                    response.TaskId,
                    response.Message
                );
            }
        }
    }

    private async Task UploadDetectionResultAndCleanup(
        UpscalingService.UpscalingServiceClient client,
        ILogger<RemoteTaskProcessor> logger,
        int taskId,
        string resultJson,
        string? downloadedFile,
        CancellationToken stoppingToken
    )
    {
        try
        {
            await client.UploadDetectionResultAsync(
                new UploadDetectionResultRequest { TaskId = taskId, ResultJson = resultJson },
                cancellationToken: stoppingToken
            );
        }
        finally
        {
            if (downloadedFile != null && File.Exists(downloadedFile))
            {
                File.Delete(downloadedFile);
            }
        }
    }

    private async Task UploadFileAndCleanup(
        UpscalingService.UpscalingServiceClient client,
        ILogger<RemoteTaskProcessor> logger,
        int taskId,
        string upscaledFile,
        string? downloadedFile,
        string? upscaledFileForCleanup,
        CancellationToken stoppingToken
    )
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
            chunks[chunk.ChunkNumber] = chunk.Chunk.ToByteArray();
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
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "mangaingestwithupscaling",
            "remoteworker"
        );
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"task_{taskId}{suffix ?? ""}.cbz");
    }

    private static UpscalerProfile GetProfileFromResponse(
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

    // Helpers
    private static void SafeDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
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
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
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
                            cancellationToken: cts.Token
                        );
                        if (!ka.IsAlive)
                        {
                            await cts.CancelAsync();
                            break;
                        }
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

    private sealed record FetchedItem(
        int TaskId,
        UpscalerProfile Profile,
        string DownloadedFile,
        CancellationTokenSource PersistentKeepAliveCts,
        Task PersistentKeepAliveTask,
        TaskType TaskType,
        string? SplitFindingsJson = null
    );

    private sealed record ProcessedItem(
        int TaskId,
        string DownloadedFile,
        string? UpscaledFile,
        string? ResultJson,
        CancellationTokenSource PersistentKeepAliveCts,
        Task PersistentKeepAliveTask,
        TaskType TaskType
    );

    /// <summary>
    ///     Implements Welford's method for online computation of statistics.
    ///     Used to estimate download and per-page processing times for predictive prefetching.
    /// </summary>
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

    /// <summary>
    ///     Implements predictive prefetch logic using statistical analysis of processing times.
    ///     Maintains rolling statistics to optimize the timing of fetch operations.
    /// </summary>
    private sealed class PrefetchPredictor
    {
        private readonly OnlineStats _downloadSeconds = new();
        private readonly OnlineStats _perPageSeconds = new();

        public void RecordDownload(TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds > 0 && double.IsFinite(elapsed.TotalSeconds))
            {
                _downloadSeconds.Add(elapsed.TotalSeconds);
            }
        }

        public void RecordPerPage(double secondsPerPage)
        {
            if (secondsPerPage > 0 && double.IsFinite(secondsPerPage))
            {
                _perPageSeconds.Add(secondsPerPage);
            }
        }

        /// <summary>
        ///     Determines whether to trigger a prefetch operation based on processing progress
        ///     and estimated completion times compared to download duration.
        /// </summary>
        public bool ShouldPrefetch(int remainingPages, int totalPages)
        {
            if (totalPages <= 0)
            {
                return false;
            }

            bool quarterLeft = remainingPages <= (int)Math.Ceiling(totalPages * 0.25);
            bool fiveLeft = remainingPages <= 5;

            bool etaTrigger = false;
            if (_downloadSeconds.Count > 0 && _perPageSeconds.Count > 0)
            {
                double download95 = _downloadSeconds.P95Upper;
                double perPage95 = _perPageSeconds.P95Upper;
                double remainingEta95 = remainingPages * perPage95;
                etaTrigger = remainingEta95 <= download95;
            }

            return quarterLeft || fiveLeft || etaTrigger;
        }
    }
}
