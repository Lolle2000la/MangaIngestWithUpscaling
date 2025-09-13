using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using CompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using ScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerMethod = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    // Predictive prefetch engine (keeps rolling stats across runs)
    private readonly PrefetchPredictor _predictor = new();

    // IDs to coordinate exclusions and lifecycle
    private int? _currentTaskId; // Task currently being processed
    private volatile bool _fetchInProgress;
    private Channel<bool>? _fetchSignals;
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
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _toUpload = Channel.CreateBounded<ProcessedItem>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _fetchSignals = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
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
        var dispatcherTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

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
                UpscaleTaskDelegationResponse resp;
                while (true)
                {
                    try
                    {
                        resp = await client.RequestUpscaleTaskWithHintAsync(new RequestTaskRequest { Prefetch = true },
                            cancellationToken: stoppingToken);
                    }
                    catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
                    {
                        // Nothing to fetch at the moment
                        await dispatcherTimer.WaitForNextTickAsync(stoppingToken);
                        continue;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to request upscale task for prefetch.");
                        _fetchInProgress = false;
                        throw;
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

                int prefetchTaskId = resp.TaskId;
                UpscalerProfile prefetchProfile = GetProfileFromResponse(resp.UpscalerProfile);

                // Start prefetch keep-alive
                var prefetchKeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                Task prefetchKeepAliveTask = RunKeepAliveLoop(prefetchKeepAliveCts, () => prefetchTaskId,
                    id => new KeepAliveRequest { TaskId = id, Prefetch = true });

                // Download file
                var sw = Stopwatch.StartNew();
                AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                    new CbzToUpscaleRequest { TaskId = resp.TaskId, Prefetch = true },
                    cancellationToken: stoppingToken);
                string file = await FetchFile(resp.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));
                sw.Stop();
                _predictor.RecordDownload(sw.Elapsed);

                // Enqueue for upscaling
                var fetched = new FetchedItem(resp.TaskId, prefetchProfile, file, prefetchKeepAliveCts);
                await _toUpscale.Writer.WriteAsync(fetched, stoppingToken);

                // Keep prefetch state until upscaler starts; it will clear and cancel keepalive.
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
                            _predictor.RecordPerPage(deltaTime / deltaPages);
                        }
                    }

                    lastProgressCurrent = current;
                    lastProgressTime = now;

                    int remaining = Math.Max(0, total - current);
                    bool shouldPrefetch = _predictor.ShouldPrefetch(remaining, total);
                    if (!signaledThisTask && shouldPrefetch)
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
                SafeDelete(item.DownloadedFile);
                SafeDelete(upscaledFile);
                _currentTaskId = null;
                await keepAliveCts.CancelAsync();
                try { await keepAliveTask; }
                catch { }

                continue;
            }
            catch (Exception ex)
            {
                // Report failure, cleanup, and continue
                var errorMessage = FormatUpscaleErrorMessage(item.TaskId, item.DownloadedFile, profile, ex);
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest { TaskId = item.TaskId, ErrorMessage = errorMessage },
                        cancellationToken: stoppingToken);
                }
                catch (Exception rpcEx)
                {
                    logger.LogWarning(rpcEx, "Failed to report task failure for {taskId}", item.TaskId);
                }

                SafeDelete(item.DownloadedFile);
                SafeDelete(upscaledFile);

                _currentTaskId = null;
                await keepAliveCts.CancelAsync();
                try { await keepAliveTask; }
                catch { }

                continue;
            }
            finally
            {
                await keepAliveCts.CancelAsync();
                try { await keepAliveTask; }
                catch { }

                // Send to upload
                await _toUpload.Writer.WriteAsync(new ProcessedItem(item.TaskId, item.DownloadedFile, upscaledFile),
                    stoppingToken);
                _currentTaskId = null;
            }
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
                var errorMessage = FormatUploadErrorMessage(item.TaskId, item.UpscaledFile, ex);
                try
                {
                    await client.ReportTaskFailedAsync(
                        new ReportTaskFailedRequest { TaskId = item.TaskId, ErrorMessage = errorMessage },
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
                    TaskId = taskId,
                    ChunkNumber = chunkNumber++,
                    Chunk = ByteString.CopyFrom(buffer, 0, bytesRead)
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

    /// <summary>
    /// Formats a comprehensive error message for upscaling failures, similar to how PythonService captures process output.
    /// </summary>
    private static string FormatUpscaleErrorMessage(int taskId, string inputFile, UpscalerProfile profile, Exception ex)
    {
        var errorBuilder = new StringBuilder();
        errorBuilder.AppendLine($"Upscaling task {taskId} failed:");
        errorBuilder.AppendLine($"Input file: {inputFile}");
        errorBuilder.AppendLine($"Profile: {profile.Name} ({profile.UpscalerMethod}, {profile.ScalingFactor}, {profile.CompressionFormat})");
        errorBuilder.AppendLine($"Quality: {profile.Quality}");

        // Check if input file exists and get its size for diagnostics
        if (File.Exists(inputFile))
        {
            try
            {
                var fileInfo = new FileInfo(inputFile);
                errorBuilder.AppendLine($"Input file size: {fileInfo.Length:N0} bytes");
            }
            catch { }
        }
        else
        {
            errorBuilder.AppendLine("Input file does not exist");
        }

        errorBuilder.AppendLine("Exception details:");
        errorBuilder.AppendLine(ex.ToString());

        return errorBuilder.ToString();
    }

    /// <summary>
    /// Formats a comprehensive error message for upload failures.
    /// </summary>
    private static string FormatUploadErrorMessage(int taskId, string upscaledFile, Exception ex)
    {
        var errorBuilder = new StringBuilder();
        errorBuilder.AppendLine($"Upload task {taskId} failed:");
        errorBuilder.AppendLine($"Upscaled file: {upscaledFile}");

        // Check if upscaled file exists and get its size for diagnostics
        if (File.Exists(upscaledFile))
        {
            try
            {
                var fileInfo = new FileInfo(upscaledFile);
                errorBuilder.AppendLine($"Upscaled file size: {fileInfo.Length:N0} bytes");
            }
            catch { }
        }
        else
        {
            errorBuilder.AppendLine("Upscaled file does not exist");
        }

        errorBuilder.AppendLine("Exception details:");
        errorBuilder.AppendLine(ex.ToString());

        return errorBuilder.ToString();
    }

    private Task RunKeepAliveLoop(CancellationTokenSource cts, Func<int?> taskIdProvider,
        Func<int, KeepAliveRequest> requestFactory)
    {
        return Task.Run(async () =>
        {
            using IServiceScope scope = serviceScopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
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

                    KeepAliveResponse? ka =
                        await client.KeepAliveAsync(requestFactory(id.Value), cancellationToken: cts.Token);
                    if (!ka.IsAlive)
                    {
                        break;
                    }
                }
                catch { }

                try { await timer.WaitForNextTickAsync(cts.Token); }
                catch { break; }
            }
        }, cts.Token);
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

    // Encapsulates predictive prefetch heuristics and rolling time statistics
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

        // Decide if we should prefetch now based on simple thresholds and ETA vs download P95
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