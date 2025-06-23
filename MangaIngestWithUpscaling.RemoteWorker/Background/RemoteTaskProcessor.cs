using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        logger.LogInformation("Successfully connected to server and waiting for work.");

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycle(stoppingToken);
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
            }
        }
    }

    private async Task RunCycle(CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();
        var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();

        var taskResponse = await client.RequestUpscaleTaskAsync(new Empty(), cancellationToken: stoppingToken);

        if (taskResponse.TaskId == -1)
        {
            return;
        }

        logger.LogInformation("Received task {taskId}.", taskResponse.TaskId);

        var profile = GetProfileFromResponse(taskResponse.UpscalerProfile);

        var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var keepAliveTask = Task.Run(async () =>
        {
            var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    var response = await client.KeepAliveAsync(new KeepAliveRequest { TaskId = taskResponse.TaskId },
                        cancellationToken: keepAliveCts.Token);
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
                    logger.LogError(ex, "Failed to send keep-alive for task {taskId}", taskResponse.TaskId);
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

        string? downloadedFile = null;
        string? upscaledFile = null;
        try
        {
            AsyncServerStreamingCall<CbzFileChunk>? stream = client.GetCbzFile(
                new CbzToUpscaleRequest { TaskId = taskResponse.TaskId },
                cancellationToken: stoppingToken);

            downloadedFile = await FetchFile(taskResponse.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));

            logger.LogInformation("Downloaded file {file} for task {taskId}.", downloadedFile, taskResponse.TaskId);

            upscaledFile = PrepareTempFile(taskResponse.TaskId, "_upscaled");
            await upscaler.Upscale(downloadedFile, upscaledFile, profile, stoppingToken);

            logger.LogInformation("Upscaled file {file} for task {taskId}.", upscaledFile, taskResponse.TaskId);

            await UploadFile(client, logger, taskResponse.TaskId, upscaledFile, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process task {taskId}.", taskResponse.TaskId);
            await client.ReportTaskFailedAsync(
                new ReportTaskFailedRequest { TaskId = taskResponse.TaskId, ErrorMessage = ex.Message },
                cancellationToken: stoppingToken);
        }
        finally
        {
            await keepAliveCts.CancelAsync();
            try
            {
                await keepAliveTask;
            }
            catch (OperationCanceledException) { }

            if (downloadedFile != null && File.Exists(downloadedFile))
            {
                File.Delete(downloadedFile);
            }

            if (upscaledFile != null && File.Exists(upscaledFile))
            {
                File.Delete(upscaledFile);
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