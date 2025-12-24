using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize(AuthenticationSchemes = "ApiKey")]
public partial class UpscalingDistributionService(
    DistributedUpscaleTaskProcessor taskProcessor,
    ApplicationDbContext dbContext,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier,
    ISplitProcessingService splitProcessingService,
    ILogger<UpscalingDistributionService> logger
) : UpscalingService.UpscalingServiceBase
{
    private static readonly string tempDir = Path.Combine(
        Path.GetTempPath(),
        "mangaingestwithupscaling"
    );
    private readonly ILogger<UpscalingDistributionService> _logger = logger;

    public override Task<CheckConnectionResponse> CheckConnection(
        Empty request,
        ServerCallContext context
    )
    {
        context.Status = new Status(StatusCode.OK, "Connection established");
        return Task.FromResult(
            new CheckConnectionResponse { Message = "Connection established", Success = true }
        );
    }

    public override Task<UpscaleTaskDelegationResponse> RequestUpscaleTask(
        Empty request,
        ServerCallContext context
    )
    {
        // Delegate to the hint-based variant, mapping header hint if provided by older clients/tools.
        bool isPrefetchHeader = context.RequestHeaders.Any(h =>
            string.Equals(h.Key, "x-prefetch", StringComparison.OrdinalIgnoreCase) && h.Value == "1"
        );
        return RequestUpscaleTaskWithHint(
            new RequestTaskRequest { Prefetch = isPrefetchHeader },
            context
        );
    }

    // New method that accepts hints in the request (e.g., prefetch) without relying on headers
    public override async Task<UpscaleTaskDelegationResponse> RequestUpscaleTaskWithHint(
        RequestTaskRequest request,
        ServerCallContext context
    )
    {
        if (request.HasPrefetch && request.Prefetch)
        {
            _logger.LogDebug("RequestUpscaleTaskWithHint called with prefetch=true");
        }

        PersistedTask? task = await taskProcessor.GetTask(context.CancellationToken);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "No tasks available");
            return new UpscaleTaskDelegationResponse { TaskId = -1, UpscalerProfile = null };
        }

        // Handle both UpscaleTask and RepairUpscaleTask
        int upscalerProfileId;
        if (task.Data is UpscaleTask upscaleTask)
        {
            upscalerProfileId = upscaleTask.UpscalerProfileId;
        }
        else if (task.Data is RepairUpscaleTask repairTask)
        {
            upscalerProfileId = repairTask.UpscalerProfileId;
        }
        else if (task.Data is DetectSplitCandidatesTask)
        {
            return new UpscaleTaskDelegationResponse
            {
                TaskId = task.Id,
                TaskType = TaskType.SplitDetection,
            };
        }
        else if (task.Data is ApplySplitsTask applySplitsTask)
        {
            var findings = await dbContext
                .StripSplitFindings.Where(f =>
                    f.ChapterId == applySplitsTask.ChapterId
                    && f.DetectorVersion == applySplitsTask.DetectorVersion
                )
                .Select(f => new SplitFindingDto
                {
                    PageFileName = f.PageFileName,
                    SplitJson = f.SplitJson,
                })
                .ToListAsync(context.CancellationToken);

            return new UpscaleTaskDelegationResponse
            {
                TaskId = task.Id,
                TaskType = TaskType.ApplySplits,
                SplitFindingsJson = JsonSerializer.Serialize(findings),
            };
        }
        else
        {
            context.Status = new Status(StatusCode.InvalidArgument, "Invalid task type");
            return new UpscaleTaskDelegationResponse { TaskId = -1, UpscalerProfile = null };
        }

        Shared.Data.LibraryManagement.UpscalerProfile? upscalerProfile =
            await dbContext.UpscalerProfiles.FindAsync(upscalerProfileId);

        if (upscalerProfile == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Upscaler profile not found");
            return new UpscaleTaskDelegationResponse { TaskId = -1, UpscalerProfile = null };
        }

        return new UpscaleTaskDelegationResponse
        {
            TaskId = task.Id,
            UpscalerProfile = new UpscalerProfile
            {
                Name = upscalerProfile.Name,
                UpscalerMethod = upscalerProfile.UpscalerMethod switch
                {
                    Shared.Data.LibraryManagement.UpscalerMethod.MangaJaNai =>
                        UpscalerMethod.MangaJaNai,
                    _ => UpscalerMethod.Unspecified,
                },
                CompressionFormat = upscalerProfile.CompressionFormat switch
                {
                    Shared.Data.LibraryManagement.CompressionFormat.Avif => CompressionFormat.Avif,
                    Shared.Data.LibraryManagement.CompressionFormat.Jpg => CompressionFormat.Jpg,
                    Shared.Data.LibraryManagement.CompressionFormat.Png => CompressionFormat.Png,
                    Shared.Data.LibraryManagement.CompressionFormat.Webp => CompressionFormat.Webp,
                    _ => CompressionFormat.Unspecified,
                },
                Quality = upscalerProfile.Quality,
                ScalingFactor = upscalerProfile.ScalingFactor switch
                {
                    Shared.Data.LibraryManagement.ScaleFactor.OneX => ScaleFactor.OneX,
                    Shared.Data.LibraryManagement.ScaleFactor.TwoX => ScaleFactor.TwoX,
                    Shared.Data.LibraryManagement.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                    Shared.Data.LibraryManagement.ScaleFactor.FourX => ScaleFactor.FourX,
                    _ => ScaleFactor.Unspecified,
                },
            },
        };
    }

    public override Task<KeepAliveResponse> KeepAlive(
        KeepAliveRequest request,
        ServerCallContext context
    )
    {
        if (taskProcessor.KeepAlive(request.TaskId))
        {
            if (
                (request.HasPrefetch && request.Prefetch)
                || context.RequestHeaders.Any(h =>
                    string.Equals(h.Key, "x-prefetch", StringComparison.OrdinalIgnoreCase)
                    && h.Value == "1"
                )
            )
            {
                _logger.LogDebug("KeepAlive received for prefetched task {taskId}", request.TaskId);
            }

            // Backward-compatible: update progress if fields are provided (proto3 optional)
            bool hasAny =
                request.HasTotal
                || request.HasCurrent
                || request.HasStatusMessage
                || request.HasPhase;
            if (hasAny)
            {
                taskProcessor.ApplyProgress(
                    request.TaskId,
                    request.HasTotal ? request.Total : null,
                    request.HasCurrent ? request.Current : null,
                    request.HasStatusMessage ? request.StatusMessage : null,
                    request.HasPhase ? request.Phase : null
                );
            }

            return Task.FromResult(new KeepAliveResponse { IsAlive = true });
        }
        else
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found or cancelled");
            return Task.FromResult(new KeepAliveResponse { IsAlive = false });
        }
    }

    public override async Task GetCbzFile(
        CbzToUpscaleRequest request,
        IServerStreamWriter<CbzFileChunk> responseStream,
        ServerCallContext context
    )
    {
        bool isPrefetchHeader = context.RequestHeaders.Any(h =>
            string.Equals(h.Key, "x-prefetch", StringComparison.OrdinalIgnoreCase) && h.Value == "1"
        );
        bool isPrefetch = isPrefetchHeader || (request.HasPrefetch && request.Prefetch);
        if (isPrefetch)
        {
            _logger.LogDebug(
                "GetCbzFile called with x-prefetch=1 for task {taskId}",
                request.TaskId
            );
        }

        var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return;
        }

        string filePath;

        // Handle different task types
        if (task.Data is UpscaleTask upscaleTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return;
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else if (task.Data is RepairUpscaleTask repairTask)
        {
            // For repair tasks, serve the prepared missing pages CBZ
            DistributedUpscaleTaskProcessor.RemoteRepairState? repairState =
                taskProcessor.GetRemoteRepairState(request.TaskId);
            if (
                repairState == null
                || string.IsNullOrEmpty(repairState.PreparedMissingPagesCbzPath)
                || !File.Exists(repairState.PreparedMissingPagesCbzPath)
            )
            {
                context.Status = new Status(
                    StatusCode.NotFound,
                    "Prepared missing pages CBZ not found"
                );
                return;
            }

            filePath = repairState.PreparedMissingPagesCbzPath;
            _logger.LogDebug(
                "Serving prepared missing pages CBZ for repair task {taskId}: {filePath}",
                request.TaskId,
                filePath
            );
        }
        else if (task.Data is DetectSplitCandidatesTask detectTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == detectTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return;
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else if (task.Data is ApplySplitsTask applySplitsTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == applySplitsTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return;
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else
        {
            context.Status = new Status(StatusCode.InvalidArgument, "Invalid task type");
            return;
        }

        if (!File.Exists(filePath))
        {
            context.Status = new Status(StatusCode.NotFound, "File not found");
            return;
        }

        await using FileStream fileStream = File.OpenRead(filePath);
        // Read the file in chunks of 1MB and stream it to the client
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            await responseStream.WriteAsync(
                new CbzFileChunk
                {
                    Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                    ChunkNumber = chunkNumber++,
                    TaskId = task.Id,
                }
            );
        }

        context.Status = new Status(StatusCode.OK, "File sent");
        return;
    }

    public override async Task<CbzFileChunk> RequestCbzFileChunk(
        CbzFileChunkRequest request,
        ServerCallContext context
    )
    {
        var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return new CbzFileChunk();
        }

        string filePath;

        // Handle different task types
        if (task.Data is UpscaleTask upscaleTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return new CbzFileChunk();
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else if (task.Data is RepairUpscaleTask repairTask)
        {
            // For repair tasks, serve the prepared missing pages CBZ
            DistributedUpscaleTaskProcessor.RemoteRepairState? repairState =
                taskProcessor.GetRemoteRepairState(request.TaskId);
            if (
                repairState == null
                || string.IsNullOrEmpty(repairState.PreparedMissingPagesCbzPath)
                || !File.Exists(repairState.PreparedMissingPagesCbzPath)
            )
            {
                context.Status = new Status(
                    StatusCode.NotFound,
                    "Prepared missing pages CBZ not found"
                );
                return new CbzFileChunk();
            }

            filePath = repairState.PreparedMissingPagesCbzPath;
        }
        else if (task.Data is DetectSplitCandidatesTask detectTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == detectTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return new CbzFileChunk();
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else if (task.Data is ApplySplitsTask applySplitsTask)
        {
            Chapter? chapter = await dbContext
                .Chapters.Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.Library)
                .FirstOrDefaultAsync(c => c.Id == applySplitsTask.ChapterId);
            if (chapter == null)
            {
                context.Status = new Status(StatusCode.NotFound, "Chapter not found");
                return new CbzFileChunk();
            }

            filePath = chapter.NotUpscaledFullPath;
        }
        else
        {
            context.Status = new Status(StatusCode.InvalidArgument, "Invalid task type");
            return new CbzFileChunk();
        }

        if (!File.Exists(filePath))
        {
            context.Status = new Status(StatusCode.NotFound, "File not found");
            return new CbzFileChunk();
        }

        await using FileStream fileStream = File.OpenRead(filePath);
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
                TaskId = task.Id,
            };
        }

        context.Status = new Status(StatusCode.Internal, "Could not open the file for some reason");
        return new CbzFileChunk();
    }

    public override async Task<UploadDetectionResultResponse> UploadDetectionResult(
        UploadDetectionResultRequest request,
        ServerCallContext context
    )
    {
        try
        {
            var task = await dbContext.PersistedTasks.FindAsync(request.TaskId);
            if (task == null)
            {
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "Task not found",
                };
            }

            if (task.Data is not DetectSplitCandidatesTask detectTask)
            {
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "Invalid task type",
                };
            }

            // Deserialize results
            var results = JsonSerializer.Deserialize<List<SplitDetectionResult>>(
                request.ResultJson
            );
            if (results == null)
            {
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "Invalid result JSON",
                };
            }

            await splitProcessingService.ProcessDetectionResultsAsync(
                detectTask.ChapterId,
                results,
                detectTask.DetectorVersion,
                context.CancellationToken
            );

            await taskProcessor.TaskCompleted(request.TaskId);

            return new UploadDetectionResultResponse
            {
                Success = true,
                Message = "Results processed",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing detection results for task {TaskId}",
                request.TaskId
            );
            return new UploadDetectionResultResponse { Success = false, Message = ex.Message };
        }
    }

    public override async Task UploadUpscaledCbzFile(
        IAsyncStreamReader<CbzFileChunk> requestStream,
        IServerStreamWriter<UploadUpscaledCbzResponse> responseStream,
        ServerCallContext context
    )
    {
        var taskChunks = new Dictionary<int, List<int>>();

        await foreach (
            CbzFileChunk request in requestStream.ReadAllAsync(context.CancellationToken)
        )
        {
            if (!taskChunks.ContainsKey(request.TaskId))
            {
                taskChunks[request.TaskId] = new List<int>();
            }

            string chunkFileName = PrepareTempChunkFile(request.TaskId, request.ChunkNumber);
            await File.WriteAllBytesAsync(
                chunkFileName,
                request.Chunk.ToByteArray(),
                context.CancellationToken
            );

            taskChunks[request.TaskId].Add(request.ChunkNumber);
        }

        foreach (var (taskId, chunks) in taskChunks)
        {
            string tempFile = PrepareTempFile(taskId);
            try
            {
                await using (FileStream fileStream = File.OpenWrite(tempFile))
                {
                    foreach (int chunkNumber in chunks.OrderBy(c => c))
                    {
                        string chunkFile = PrepareTempChunkFile(taskId, chunkNumber);
                        await using (FileStream chunkStream = File.OpenRead(chunkFile))
                        {
                            await chunkStream.CopyToAsync(fileStream, context.CancellationToken);
                        }

                        File.Delete(chunkFile);
                    }
                }

                PersistedTask? task = await dbContext.PersistedTasks.FindAsync(taskId);
                if (task == null)
                {
                    File.Delete(tempFile);
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = false,
                            Message = "Task not found",
                            TaskId = taskId,
                        }
                    );
                    continue;
                }

                // Handle different task types
                if (task.Data is UpscaleTask upscaleTask)
                {
                    Chapter? chapter = await dbContext
                        .Chapters.Include(chapter => chapter.Manga)
                            .ThenInclude(manga => manga.Library)
                        .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId);

                    if (chapter == null)
                    {
                        File.Delete(tempFile);
                        await responseStream.WriteAsync(
                            new UploadUpscaledCbzResponse
                            {
                                Success = false,
                                Message = "Chapter not found",
                                TaskId = taskId,
                            }
                        );
                        continue;
                    }

                    if (chapter.UpscaledFullPath == null)
                    {
                        File.Delete(tempFile);
                        await responseStream.WriteAsync(
                            new UploadUpscaledCbzResponse
                            {
                                Success = false,
                                Message = "Suitable location to save the chapter not found.",
                                TaskId = taskId,
                            }
                        );
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
                    await dbContext.SaveChangesAsync();
                    await taskProcessor.TaskCompleted(taskId);
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = true,
                            Message = "Chapter upscaled",
                            TaskId = taskId,
                        }
                    );
                    _ = chapterChangedNotifier.Notify(chapter, true);
                }
                else if (task.Data is RepairUpscaleTask repairTask)
                {
                    // For repair tasks, save the upscaled result to the designated repair path
                    DistributedUpscaleTaskProcessor.RemoteRepairState? repairState =
                        taskProcessor.GetRemoteRepairState(taskId);
                    if (
                        repairState == null
                        || string.IsNullOrEmpty(repairState.UpscaledMissingPagesCbzPath)
                    )
                    {
                        File.Delete(tempFile);
                        await responseStream.WriteAsync(
                            new UploadUpscaledCbzResponse
                            {
                                Success = false,
                                Message =
                                    "No upscaled missing pages path specified for repair task.",
                                TaskId = taskId,
                            }
                        );
                        continue;
                    }

                    string? destinationDirectory = Path.GetDirectoryName(
                        repairState.UpscaledMissingPagesCbzPath
                    );
                    if (destinationDirectory != null)
                    {
                        fileSystem.CreateDirectory(destinationDirectory);
                    }

                    if (File.Exists(repairState.UpscaledMissingPagesCbzPath))
                    {
                        File.Delete(repairState.UpscaledMissingPagesCbzPath);
                    }

                    fileSystem.Move(tempFile, repairState.UpscaledMissingPagesCbzPath);

                    // Save the updated task data with the upscaled file path
                    await dbContext.SaveChangesAsync();
                    await taskProcessor.TaskCompleted(taskId);
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = true,
                            Message = "Repair missing pages upscaled",
                            TaskId = taskId,
                        }
                    );
                }
                else if (task.Data is ApplySplitsTask applySplitsTask)
                {
                    Chapter? chapter = await dbContext
                        .Chapters.Include(chapter => chapter.Manga)
                            .ThenInclude(manga => manga.Library)
                        .FirstOrDefaultAsync(c => c.Id == applySplitsTask.ChapterId);

                    if (chapter == null)
                    {
                        File.Delete(tempFile);
                        await responseStream.WriteAsync(
                            new UploadUpscaledCbzResponse
                            {
                                Success = false,
                                Message = "Chapter not found",
                                TaskId = taskId,
                            }
                        );
                        continue;
                    }

                    // Replace original file
                    if (File.Exists(chapter.NotUpscaledFullPath))
                    {
                        File.Delete(chapter.NotUpscaledFullPath);
                    }

                    fileSystem.Move(tempFile, chapter.NotUpscaledFullPath);

                    // Update state
                    var state = await dbContext.ChapterSplitProcessingStates.FirstOrDefaultAsync(
                        s => s.ChapterId == applySplitsTask.ChapterId
                    );

                    if (state != null)
                    {
                        state.Status = SplitProcessingStatus.Applied;
                        state.LastAppliedDetectorVersion = applySplitsTask.DetectorVersion;
                    }
                    else
                    {
                        dbContext.ChapterSplitProcessingStates.Add(
                            new ChapterSplitProcessingState
                            {
                                ChapterId = applySplitsTask.ChapterId,
                                Status = SplitProcessingStatus.Applied,
                                LastAppliedDetectorVersion = applySplitsTask.DetectorVersion,
                            }
                        );
                    }

                    await dbContext.SaveChangesAsync();
                    await taskProcessor.TaskCompleted(taskId);
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = true,
                            Message = "Splits applied",
                            TaskId = taskId,
                        }
                    );
                    _ = chapterChangedNotifier.Notify(chapter, false);
                }
                else
                {
                    File.Delete(tempFile);
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = false,
                            Message = "Invalid task type",
                            TaskId = taskId,
                        }
                    );
                    continue;
                }
            }
            catch (Exception ex)
            {
                context.Status = new Status(StatusCode.Internal, ex.Message);
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = false,
                        Message = ex.Message,
                        TaskId = taskId,
                    }
                );
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        context.Status = new Status(StatusCode.OK, "File(s) uploaded");
    }

    public override async Task<Empty> ReportTaskFailed(
        ReportTaskFailedRequest request,
        ServerCallContext context
    )
    {
        await taskProcessor.TaskFailed(request.TaskId, request.ErrorMessage);
        return new Empty();
    }

    private string PrepareTempFile(int taskId)
    {
        fileSystem.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"upscaled_{taskId}.cbz");
    }

    private string PrepareTempChunkFile(int taskId, int chunkNumber)
    {
        fileSystem.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"upscaled_{taskId}_{chunkNumber}.chunk");
    }
}
