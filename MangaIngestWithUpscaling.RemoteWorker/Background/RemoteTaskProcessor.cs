using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class TaskData
{
    public UpscaleTaskDelegationResponse TaskResponse { get; set; } = null!;
    public string? DownloadedFile { get; set; }
    public string? UpscaledFile { get; set; }
    public UpscalerProfile Profile { get; set; } = null!;
}

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        logger.LogInformation("Successfully connected to server and waiting for work.");

        TaskData? nextTask = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Process the task we have ready (either from previous prefetch or newly fetched)
                TaskData? currentTask = nextTask ?? await TryGetNextTask(stoppingToken);
                nextTask = null;

                if (currentTask == null)
                {
                    // No task available, wait a bit before trying again
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                // Start prefetching the next task while we process the current one
                var nextTaskFetch = TryGetNextTask(stoppingToken);

                // Process the current task
                await ProcessTask(currentTask, stoppingToken);

                // Wait for the next task prefetch to complete (if it hasn't already)
                try
                {
                    nextTask = await nextTaskFetch;
                }
                catch (Exception ex)
                {
                    // Log error but don't fail the main loop - we'll try again next iteration
                    logger.LogWarning(ex, "Failed to prefetch next task, will retry on next iteration");
                    nextTask = null;
                }
            }
            catch (RpcException ex)
            {
                switch (ex.StatusCode)
                {
                    case StatusCode.Unavailable:
                        // The server is unavailable, wait for a bit before trying again
                        logger.LogWarning("Server unavailable, retrying in 5 seconds.");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    case StatusCode.NotFound:
                        // No task was received, which is to be expected
                        logger.LogDebug("No task received: {message}", ex.Status.Detail);
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    default:
                        // Log other RPC exceptions
                        logger.LogError(ex, "RPC error occurred: {message}", ex.Status.Detail);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Log the exception and continue
                logger.LogError(ex, "Failed to process remote task.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<TaskData?> TryGetNextTask(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        var taskResponse = await client.RequestUpscaleTaskAsync(new Empty(), cancellationToken: stoppingToken);

        if (taskResponse.TaskId == -1)
        {
            return null;
        }

        logger.LogInformation("Received task {taskId}.", taskResponse.TaskId);

        var profile = GetProfileFromResponse(taskResponse.UpscalerProfile);

        var taskData = new TaskData
        {
            TaskResponse = taskResponse,
            Profile = profile
        };

        // Download the file immediately when we get a task
        try
        {
            AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                new CbzToUpscaleRequest { TaskId = taskResponse.TaskId },
                cancellationToken: stoppingToken);

            taskData.DownloadedFile = await FetchFile(taskResponse.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));
            
            logger.LogInformation("Downloaded file {file} for task {taskId}.", taskData.DownloadedFile, taskResponse.TaskId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download file for task {taskId}.", taskResponse.TaskId);
            
            // Report task failure to server
            try
            {
                await client.ReportTaskFailedAsync(
                    new ReportTaskFailedRequest { TaskId = taskResponse.TaskId, ErrorMessage = $"Failed to download file: {ex.Message}" });
            }
            catch (Exception reportEx)
            {
                logger.LogError(reportEx, "Failed to report task failure for task {taskId}.", taskResponse.TaskId);
            }

            return null;
        }

        return taskData;
    }

    private async Task ProcessTask(TaskData taskData, CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();

        var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var keepAliveTask = Task.Run(async () =>
        {
            var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    var response = await client.KeepAliveAsync(new KeepAliveRequest { TaskId = taskData.TaskResponse.TaskId },
                        cancellationToken: keepAliveCts.Token);
                    if (!response.IsAlive)
                    {
                        logger.LogWarning(
                            "Keep-alive for task {taskId} failed. Server reports task is no longer alive. Aborting.",
                            taskData.TaskResponse.TaskId);
                        await keepAliveCts.CancelAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send keep-alive for task {taskId}", taskData.TaskResponse.TaskId);
                }

                if (keepAliveCts.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await keepAliveTimer.WaitForNextTickAsync(keepAliveCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, keepAliveCts.Token);

        try
        {
            // File is already downloaded, just process it
            taskData.UpscaledFile = PrepareTempFile(taskData.TaskResponse.TaskId, "_upscaled");
            await upscaler.Upscale(taskData.DownloadedFile!, taskData.UpscaledFile, taskData.Profile, stoppingToken);

            logger.LogInformation("Upscaled file {file} for task {taskId}.", taskData.UpscaledFile, taskData.TaskResponse.TaskId);

            await UploadFile(client, logger, taskData.TaskResponse.TaskId, taskData.UpscaledFile, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {taskId} was cancelled.", taskData.TaskResponse.TaskId);
            // Do not report failure, just exit gracefully
            // Would otherwise mark the task as failed on the server
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process task {taskId}.", taskData.TaskResponse.TaskId);
            await client.ReportTaskFailedAsync(
                new ReportTaskFailedRequest { TaskId = taskData.TaskResponse.TaskId, ErrorMessage = ex.Message });
        }
        finally
        {
            await keepAliveCts.CancelAsync();
            try
            {
                await keepAliveTask;
            }
            catch (OperationCanceledException) { }

            // Clean up files
            if (taskData.DownloadedFile != null && File.Exists(taskData.DownloadedFile))
            {
                File.Delete(taskData.DownloadedFile);
            }

            if (taskData.UpscaledFile != null && File.Exists(taskData.UpscaledFile))
            {
                File.Delete(taskData.UpscaledFile);
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

    private async Task<string> FetchFile(int taskId, IAsyncEnumerable<CbzFileChunk> stream)
    {
        Dictionary<int, byte[]> chunks = new();

        await foreach (var chunk in stream)
        {
            chunks.Add(chunk.ChunkNumber, chunk.Chunk.ToByteArray());
        }

        var tempFile = PrepareTempFile(taskId);

        // Sort the files by chunk number and merge them into a single file using binary concatenation
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
                CompressionFormat.Webp => Shared.Data.LibraryManagement.CompressionFormat.Webp,
                CompressionFormat.Png => Shared.Data.LibraryManagement.CompressionFormat.Png,
                CompressionFormat.Jpg => Shared.Data.LibraryManagement.CompressionFormat.Jpg,
                CompressionFormat.Avif => Shared.Data.LibraryManagement.CompressionFormat.Avif,
                _ => throw new InvalidOperationException("Unknown compression format.")
            },
            Name = upscalerProfile.Name,
            Quality = upscalerProfile.Quality,
            ScalingFactor = upscalerProfile.ScalingFactor switch
            {
                ScaleFactor.OneX => Shared.Data.LibraryManagement.ScaleFactor.OneX,
                ScaleFactor.TwoX => Shared.Data.LibraryManagement.ScaleFactor.TwoX,
                ScaleFactor.ThreeX => Shared.Data.LibraryManagement.ScaleFactor.ThreeX,
                ScaleFactor.FourX => Shared.Data.LibraryManagement.ScaleFactor.FourX,
                _ => throw new InvalidOperationException("Unknown scaling factor.")
            },
            UpscalerMethod = upscalerProfile.UpscalerMethod switch
            {
                UpscalerMethod.MangaJaNai => Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai,
                _ => throw new InvalidOperationException("Unknown upscaler method.")
            }
        };
    }
}