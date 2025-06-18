using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize(AuthenticationSchemes = "ApiKey")]
public partial class UpscalingDistributionService(
    TaskQueue taskQueue,
    DistributedUpscaleTaskProcessor taskProcessor,
    ApplicationDbContext dbContext,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier) : UpscalingService.UpscalingServiceBase
{
    private static readonly string tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling");

    public override Task<CheckConnectionResponse> CheckConnection(Empty request, ServerCallContext context)
    {
        context.Status = new Status(StatusCode.OK, "Connection established");
        return Task.FromResult(new CheckConnectionResponse { Message = "Connection established", Success = true });
    }

    public override async Task<UpscaleTaskDelegationResponse> RequestUpscaleTask(Empty request,
        ServerCallContext context)
    {
        var task = await taskProcessor.GetTask(context.CancellationToken);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "No tasks available");
            return new UpscaleTaskDelegationResponse { TaskId = -1, UpscalerProfile = null };
        }
        else
        {
            var upscaleTask = (UpscaleTask)task.Data;
            var upscalerProfile = await dbContext.UpscalerProfiles.FindAsync(upscaleTask.UpscalerProfileId);

            if (upscalerProfile == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Upscaler profile not found");
                return new UpscaleTaskDelegationResponse { TaskId = -1, UpscalerProfile = null };
            }

            return new UpscaleTaskDelegationResponse()
            {
                TaskId = task.Id,
                UpscalerProfile = new UpscalerProfile()
                {
                    Name = upscalerProfile.Name,
                    UpscalerMethod = upscalerProfile.UpscalerMethod switch
                    {
                        Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                        _ => UpscalerMethod.Unspecified
                    },
                    CompressionFormat = upscalerProfile.CompressionFormat switch
                    {
                        Shared.Data.LibraryManagement.CompressionFormat.Avif => CompressionFormat.Avif,
                        Shared.Data.LibraryManagement.CompressionFormat.Jpg => CompressionFormat.Jpg,
                        Shared.Data.LibraryManagement.CompressionFormat.Png => CompressionFormat.Png,
                        Shared.Data.LibraryManagement.CompressionFormat.Webp => CompressionFormat.Webp,
                        _ => CompressionFormat.Unspecified
                    },
                    Quality = upscalerProfile.Quality,
                    ScalingFactor = upscalerProfile.ScalingFactor switch
                    {
                        Shared.Data.LibraryManagement.ScaleFactor.OneX => ScaleFactor.OneX,
                        Shared.Data.LibraryManagement.ScaleFactor.TwoX => ScaleFactor.TwoX,
                        Shared.Data.LibraryManagement.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                        Shared.Data.LibraryManagement.ScaleFactor.FourX => ScaleFactor.FourX,
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

    public override async Task GetCbzFile(CbzToUpscaleRequest request, IServerStreamWriter<CbzFileChunk> responseStream,
        ServerCallContext context)
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
                Chunk = ByteString.CopyFrom(buffer, 0, bytesRead), ChunkNumber = chunkNumber++, TaskId = task.Id
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
                Chunk = ByteString.CopyFrom(buffer, 0, bytesRead), ChunkNumber = chunkNumber++, TaskId = task.Id
            };
        }

        context.Status = new Status(StatusCode.Internal, "Could not open the file for some reason");
        return new CbzFileChunk();
    }

    public override async Task UploadUpscaledCbzFile(IAsyncStreamReader<CbzFileChunk> requestStream,
        IServerStreamWriter<UploadUpscaledCbzResponse> responseStream, ServerCallContext context)
    {
        var taskChunks = new Dictionary<int, Dictionary<int, byte[]>>();

        await foreach (var request in requestStream.ReadAllAsync())
        {
            if (!taskChunks.ContainsKey(request.TaskId))
            {
                taskChunks[request.TaskId] = new Dictionary<int, byte[]>();
            }

            taskChunks[request.TaskId][request.ChunkNumber] = request.Chunk.ToByteArray();
        }

        foreach (var (taskId, chunks) in taskChunks)
        {
            string tempFile = PrepareTempFile(taskId);
            try
            {
                await using (FileStream fileStream = File.OpenWrite(tempFile))
                {
                    foreach (KeyValuePair<int, byte[]> chunk in chunks.OrderBy(c => c.Key))
                    {
                        await fileStream.WriteAsync(chunk.Value);
                    }
                }

                PersistedTask? task = await dbContext.PersistedTasks.FindAsync(taskId);
                if (task == null)
                {
                    File.Delete(tempFile);
                    await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                    {
                        Success = false, Message = "Task not found", TaskId = taskId
                    });
                    continue;
                }

                var upscaleTask = (UpscaleTask)task.Data;
                Chapter? chapter = await dbContext.Chapters
                    .Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                    .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId);

                if (chapter == null)
                {
                    File.Delete(tempFile);
                    await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                    {
                        Success = false, Message = "Chapter not found", TaskId = taskId
                    });
                    continue;
                }

                if (chapter.UpscaledFullPath == null)
                {
                    File.Delete(tempFile);
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

                string? destinationDirectory = Path.GetDirectoryName(chapter.UpscaledFullPath);
                if (destinationDirectory != null)
                {
                    fileSystem.CreateDirectory(destinationDirectory);
                }

                fileSystem.Move(tempFile, chapter.UpscaledFullPath);
                chapter.IsUpscaled = true;
                chapter.UpscalerProfileId = upscaleTask.UpscalerProfileId;
                dbContext.Update(chapter);
                await dbContext.SaveChangesAsync();
                await taskProcessor.TaskCompleted(taskId);
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = true, Message = "Chapter upscaled", TaskId = taskId
                });
                _ = chapterChangedNotifier.Notify(chapter, true);
            }
            catch (Exception ex)
            {
                context.Status = new Status(StatusCode.Internal, ex.Message);
                await responseStream.WriteAsync(new UploadUpscaledCbzResponse
                {
                    Success = false, Message = ex.Message, TaskId = taskId
                });
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        context.Status = new Status(StatusCode.OK, "File(s) uploaded");
    }

    private string PrepareTempFile(int taskId)
    {
        fileSystem.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"upscaled_{taskId}.cbz");
    }
}