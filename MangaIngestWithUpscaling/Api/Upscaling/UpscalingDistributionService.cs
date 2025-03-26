using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize(AuthenticationSchemes = "ApiKey")]
public partial class UpscalingDistributionService(
    TaskQueue taskQueue,
    DistributedUpscaleTaskProcessor taskProcessor,
    ApplicationDbContext dbContext,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : UpscalingService.UpscalingServiceBase
{
    public override Task<CheckConnectionResponse> CheckConnection(Empty request, ServerCallContext context)
    {
        context.Status = new Status(StatusCode.OK, "Connection established");
        return Task.FromResult(new CheckConnectionResponse()
        {
            Message = "Connection established",
            Success = true
        });
    }

    public override async Task<UpscaleTaskDelegationResponse> RequestUpscaleTask(Empty request, ServerCallContext context)
    {
        var task = await taskProcessor.GetTask(context.CancellationToken);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "No tasks available");
            return new UpscaleTaskDelegationResponse()
            {
                TaskId = -1,
                UpscalerProfile = null
            };
        }
        else
        {
            var upscaleTask = (UpscaleTask)task.Data;
            var upscalerProfile = await dbContext.UpscalerProfiles.FindAsync(upscaleTask.UpscalerProfileId);

            if (upscalerProfile == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Upscaler profile not found");
                return new UpscaleTaskDelegationResponse()
                {
                    TaskId = -1,
                    UpscalerProfile = null
                };
            }

            return new UpscaleTaskDelegationResponse()
            {
                TaskId = task.Id,
                UpscalerProfile = new UpscalerProfile()
                {
                    Name = upscalerProfile.Name,
                    UpscalerMethod = upscalerProfile.UpscalerMethod switch
                    {
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                        _ => UpscalerMethod.Unspecified
                    },
                    CompressionFormat = upscalerProfile.CompressionFormat switch
                    {
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat.Avif => CompressionFormat.Avif,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat.Jpg => CompressionFormat.Jpg,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat.Png => CompressionFormat.Png,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat.Webp => CompressionFormat.Webp,
                        _ => CompressionFormat.Unspecified
                    },
                    Quality = upscalerProfile.Quality,
                    ScalingFactor = upscalerProfile.ScalingFactor switch
                    {
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor.OneX => ScaleFactor.OneX,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor.TwoX => ScaleFactor.TwoX,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                        MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor.FourX => ScaleFactor.FourX,
                        _ => ScaleFactor.Unspecified
                    }
                },
            };
        }
    }

    public override async Task<KeepAliveResponse> KeepAlive(KeepAliveRequest request, ServerCallContext context)
    {
        if (taskProcessor.KeepAlive(request.TaskId))
        {
            return new KeepAliveResponse { IsAlive = true };
        }
        else
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return new KeepAliveResponse { IsAlive = false };
        }
    }

    public override async Task GetCbzFile(CbzToUpscaleRequest request, IServerStreamWriter<CbzFileChunk> responseStream, ServerCallContext context)
    {
        var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return;
        }

        var cbzTask = (UpscaleTask)task.Data;
        var chapter = await dbContext.Chapters
            .Include(chapter => chapter.Manga)
                .ThenInclude(manga => manga.Library)
            .FirstOrDefaultAsync(c => c.Id == cbzTask.ChapterId);
        if (chapter == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Chapter not found");
            return;
        }
        if (!File.Exists(chapter.NotUpscaledFullPath))
        {
            context.Status = new Status(StatusCode.NotFound, "Chapter not found");
            return;
        }

        using var fileStream = File.OpenRead(chapter.NotUpscaledFullPath);
        // Read the file in chunks of 1MB and stream it to the client
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            await responseStream.WriteAsync(new CbzFileChunk
            {
                Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                ChunkNumber = chunkNumber++,
                TaskId = task.Id
            });
        }

        context.Status = new Status(StatusCode.OK, "File sent");
        return;
    }

    public override async Task<CbzFileChunk> RequestCbzFileChunk(CbzFileChunkRequest request, ServerCallContext context)
    {
        var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return new CbzFileChunk();
        }

        var cbzTask = (UpscaleTask)task.Data;
        var chapter = await dbContext.Chapters
            .Include(chapter => chapter.Manga)
                .ThenInclude(manga => manga.Library)
            .FirstOrDefaultAsync(c => c.Id == cbzTask.ChapterId);
        if (chapter == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Chapter not found");
            return new CbzFileChunk();
        }
        if (!File.Exists(chapter.NotUpscaledFullPath))
        {
            context.Status = new Status(StatusCode.NotFound, "Chapter not found");
            return new CbzFileChunk();
        }

        using var fileStream = File.OpenRead(chapter.NotUpscaledFullPath);
        // Read the file in chunks of 1MB and stream it to the client
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        var offset = request.ChunkNumber * buffer.Length;
        fileStream.Seek(offset, SeekOrigin.Begin);
        if ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            context.Status = new Status(StatusCode.OK, "Chunk sent");
            return new CbzFileChunk
            {
                Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                ChunkNumber = chunkNumber++,
                TaskId = task.Id
            };
        }

        context.Status = new Status(StatusCode.Internal, "Could not open the file for some reason");
        return new CbzFileChunk();
    }

    public override async Task UploadUpscaledCbzFile(IAsyncStreamReader<CbzFileChunk> requestStream, IServerStreamWriter<UploadUpscaledCbzResponse> responseStream, ServerCallContext context)
    {
        List<(int, int)> taskChunkPairs = new();

        await foreach (var request in requestStream.ReadAllAsync())
        {
            var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
            if (task == null)
            {
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Task not found",
                    TaskId = request.TaskId
                });
            }
            var cbzTask = (UpscaleTask)task.Data;
            var chapter = await dbContext.Chapters
                .Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == cbzTask.ChapterId);
            if (chapter == null)
            {
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Chapter not found",
                    TaskId = request.TaskId
                });
            }
            var tempFile = PrepareTempChunkFile(task.Id, request.ChunkNumber);
            using var fileStream = File.OpenWrite(tempFile);
            await fileStream.WriteAsync(request.Chunk.ToByteArray().AsMemory(0, request.Chunk.Length));
            if (request.ChunkNumber == 0)
            {
                // First chunk, create the file
                fileStream.SetLength(0);
            }
            taskChunkPairs.Add((task.Id, request.ChunkNumber));
        }

        var taskChunksDict = taskChunkPairs
            .GroupBy(x => x.Item1)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Item2).ToList());

        MergeChunks(taskChunksDict);

        foreach (var (taskId, _) in taskChunkPairs)
        {
            var task = await dbContext.PersistedTasks.FindAsync(taskId);
            var upscaleTask = (UpscaleTask)task!.Data;
            var chapter = await dbContext.Chapters
                .Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId);

            if (chapter == null)
            {
                File.Delete(PrepareTempFile(taskId));
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Chapter not found",
                    TaskId = taskId
                });
                continue;
            }
            if (chapter.UpscaledFullPath == null)
            {
                File.Delete(PrepareTempFile(taskId));
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Suitable location to save the chapter not found.",
                    TaskId = taskId
                });
                continue;
            }

            if (File.Exists(chapter.UpscaledFullPath))
            {
                File.Delete(chapter.UpscaledFullPath);
            }

            try
            {
                fileSystem.Move(PrepareTempFile(taskId), chapter.UpscaledFullPath);
                chapter.IsUpscaled = true;
                chapter.UpscalerProfileId = upscaleTask.UpscalerProfileId;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync();
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = true,
                    Message = "Chapter upscaled",
                    TaskId = taskId
                });
                _ = chapterChangedNotifier.Notify(chapter, true);
            }
            catch (Exception ex)
            {
                context.Status = new Status(StatusCode.Internal, ex.Message);
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TaskId = taskId
                });
                if (File.Exists(PrepareTempFile(taskId)))
                {
                    File.Delete(PrepareTempFile(taskId));
                }
            }
        }

        context.Status = new Status(StatusCode.OK, "File(s) uploaded");
    }

    private string PrepareTempChunkFile(int taskId, int chunk)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling");
        fileSystem.CreateDirectory(tempDir);
        return Path.Combine(Path.GetTempPath(), $"upscaled_{taskId}_{chunk}.cbz");
    }

    private string PrepareTempFile(int taskId)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling");
        fileSystem.CreateDirectory(tempDir);
        return Path.Combine(Path.GetTempPath(), $"upscaled_{taskId}.cbz");
    }

    private void MergeChunks(Dictionary<int, List<int>> taskChunksDict)
    {
        foreach (var (taskId, chunks) in taskChunksDict)
        {
            var tempFile = PrepareTempFile(taskId);
            using var fileStream = File.OpenWrite(tempFile);
            foreach (var chunk in chunks)
            {
                var chunkFile = PrepareTempChunkFile(taskId, chunk);
                using (var chunkFileStream = File.OpenRead(chunkFile))
                {
                    chunkFileStream.CopyTo(fileStream);
                }
                File.Delete(chunkFile);
            }
        }
    }
}
