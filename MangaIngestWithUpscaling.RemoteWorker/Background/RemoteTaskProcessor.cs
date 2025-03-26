using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

public class RemoteTaskProcessor(
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        using var scope = serviceScopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RemoteTaskProcessor>>();

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycle(stoppingToken);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                // The server is unavailable, wait for a bit before trying again
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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

        var taskResponse = await client.RequestUpscaleTaskAsync(new Empty(), cancellationToken: stoppingToken);

        if (taskResponse.TaskId == -1)
        {
            return;
        }

        var profile = GetProfileFromResponse(taskResponse.UpscalerProfile);

        var stream = client.GetCbzFile(new CbzToUpscaleRequest() { TaskId = taskResponse.TaskId }, cancellationToken: stoppingToken);

        var mergedFile = await FetchFile(taskResponse.TaskId, stream.ResponseStream.ReadAllAsync(stoppingToken));

        logger.LogInformation("Merged file {file} for task {taskId}.", mergedFile, taskResponse.TaskId);
    }

    private async Task<string> FetchFile(int taskId, IAsyncEnumerable<CbzFileChunk> stream)
    {
        Dictionary<int, byte[]> chunks = new();

        await foreach (var chunk in stream)
        {
            Console.WriteLine($"Received chunk {chunk.ChunkNumber}.");
            chunks.Add(chunk.ChunkNumber, chunk.Chunk.ToByteArray());
        }

        var tempFile = PrepareTempFile(taskId);

        // Sort the files by chunk number and merge them into a single file using binary concatenation
        using (var output = File.OpenWrite(tempFile))
        {
            foreach (var chunk in chunks.OrderBy(c => c.Key))
            {
                output.Write(chunk.Value, 0, chunk.Value.Length);
            }
        }

        Console.WriteLine($"Merged file {tempFile}.");

        return tempFile;
    }

    private string PrepareTempFile(int taskId)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(Path.GetTempPath(), $"upscaled_{taskId}.cbz");
    }

    private static Shared.Data.LibraryManagement.UpscalerProfile GetProfileFromResponse(Api.Upscaling.UpscalerProfile upscalerProfile)
    {
        return new Shared.Data.LibraryManagement.UpscalerProfile()
        {
            CompressionFormat = upscalerProfile.CompressionFormat switch
            {
                Api.Upscaling.CompressionFormat.Webp => Shared.Data.LibraryManagement.CompressionFormat.Webp,
                Api.Upscaling.CompressionFormat.Png => Shared.Data.LibraryManagement.CompressionFormat.Png,
                Api.Upscaling.CompressionFormat.Jpg => Shared.Data.LibraryManagement.CompressionFormat.Jpg,
                Api.Upscaling.CompressionFormat.Avif => Shared.Data.LibraryManagement.CompressionFormat.Avif,
                _ => throw new InvalidOperationException("Unknown compression format.")
            },
            Name = upscalerProfile.Name,
            Quality = upscalerProfile.Quality,
            ScalingFactor = upscalerProfile.ScalingFactor switch
            {
                Api.Upscaling.ScaleFactor.OneX => Shared.Data.LibraryManagement.ScaleFactor.OneX,
                Api.Upscaling.ScaleFactor.TwoX => Shared.Data.LibraryManagement.ScaleFactor.TwoX,
                Api.Upscaling.ScaleFactor.ThreeX => Shared.Data.LibraryManagement.ScaleFactor.ThreeX,
                Api.Upscaling.ScaleFactor.FourX => Shared.Data.LibraryManagement.ScaleFactor.FourX,
                _ => throw new InvalidOperationException("Unknown scaling factor.")
            },
            UpscalerMethod = upscalerProfile.UpscalerMethod switch
            {
                Api.Upscaling.UpscalerMethod.MangaJaNai => Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai,
                _ => throw new InvalidOperationException("Unknown upscaler method.")
            }
        };
    }
}
