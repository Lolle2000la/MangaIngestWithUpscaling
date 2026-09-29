using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Services.Uploads;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.Uploads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize(AuthenticationSchemes = "ApiKey")]
public partial class UpscalingDistributionService(
    DistributedUpscaleTaskProcessor taskProcessor,
    ApplicationDbContext dbContext,
    IFileSystem fileSystem,
    IChapterChangedNotifier chapterChangedNotifier,
    ISplitProcessingService splitProcessingService,
    ISplitProcessingCoordinator splitProcessingCoordinator,
    ResumableUploadStore uploadStore,
    IOptions<UploadsConfig> uploadsConfig,
    ILogger<UpscalingDistributionService> logger
) : UpscalingService.UpscalingServiceBase
{
    private static readonly string tempDir = Path.Combine(
        Path.GetTempPath(),
        "mangaingestwithupscaling"
    );

    // Serializes assembling and completing a task so a client retry can't process the same upload
    // twice. Striped, so the number of gates stays bounded no matter how many tasks a long-running
    // server processes.
    private const int UploadProcessingLockCount = 64;
    private static readonly SemaphoreSlim[] uploadProcessingGates = Enumerable
        .Range(0, UploadProcessingLockCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
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

        var response = new UpscaleTaskDelegationResponse
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

        if (await GetTaskFileSizeAsync(task, context.CancellationToken) is { } bytes)
        {
            response.InputSizeBytes = bytes;
        }

        return response;
    }

    public override async Task<PeekNextTaskResponse> PeekNextTask(
        Empty request,
        ServerCallContext context
    )
    {
        PersistedTask? task = taskProcessor.PeekTask();
        if (task == null)
        {
            return new PeekNextTaskResponse { TaskId = -1 };
        }

        var response = new PeekNextTaskResponse { TaskId = task.Id };
        if (await GetTaskFileSizeAsync(task, context.CancellationToken) is { } bytes)
        {
            response.InputSizeBytes = bytes;
        }

        return response;
    }

    /// <summary>
    /// Resolves the size of the input CBZ for a task, or null when the task has no single input
    /// file or its referenced data is missing.
    /// </summary>
    private async Task<long?> GetTaskFileSizeAsync(PersistedTask task, CancellationToken ct)
    {
        string? filePath = await ResolveTaskFilePathAsync(task, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        return new FileInfo(filePath).Length;
    }

    private async Task<string?> ResolveTaskFilePathAsync(PersistedTask task, CancellationToken ct)
    {
        return task.Data switch
        {
            UpscaleTask upscaleTask => await GetChapterFilePathAsync(upscaleTask.ChapterId, ct),
            DetectSplitCandidatesTask detectTask => await GetChapterFilePathAsync(
                detectTask.ChapterId,
                ct
            ),
            ApplySplitsTask applySplitsTask => await GetChapterFilePathAsync(
                applySplitsTask.ChapterId,
                ct
            ),
            RepairUpscaleTask => taskProcessor
                .GetRemoteRepairState(task.Id)
                ?.PreparedMissingPagesCbzPath,
            _ => null,
        };
    }

    private async Task<string?> GetChapterFilePathAsync(int chapterId, CancellationToken ct)
    {
        Chapter? chapter = await dbContext
            .Chapters.Include(chapter => chapter.Manga)
                .ThenInclude(manga => manga.Library)
            .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        return chapter?.NotUpscaledFullPath;
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
        if (task == null || task.Status == PersistedTaskStatus.Canceled)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found or cancelled");
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

        await using FileStream fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous
        );
        // Read the file in chunks of 1MB and stream it to the client
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        while (
            (
                bytesRead = await fileStream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    context.CancellationToken
                )
            ) > 0
        )
        {
            if (
                context.CancellationToken.IsCancellationRequested
                || !taskProcessor.IsRunningRemotely(task.Id)
            )
            {
                context.Status = new Status(StatusCode.NotFound, "Task cancelled");
                return;
            }

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
        if (task == null || task.Status == PersistedTaskStatus.Canceled)
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found or cancelled");
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

        await using FileStream fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous
        );
        // Read the file in chunks of 1MB and stream it to the client
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        int chunkNumber = 0;
        var offset = request.ChunkNumber * buffer.Length;
        fileStream.Seek(offset, SeekOrigin.Begin);
        if (
            (
                bytesRead = await fileStream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    context.CancellationToken
                )
            ) > 0
        )
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
            if (task == null || task.Status == PersistedTaskStatus.Canceled)
            {
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "Task not found or cancelled",
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
        // Chunks may span multiple tasks in a single call; group them per task so each task can be
        // assembled once its contiguous run is complete.
        var taskUploads = new Dictionary<int, TaskUploadState>();

        // The gRPC request-body cap is lifted for uploads, so these bounds (plus accepting chunks
        // only for real tasks) are what keep an authenticated worker from filling the disk before
        // the periodic sweep. Reject anything outside a sane range.
        int maxTotalChunks = uploadsConfig.Value.MaxTotalChunks;
        int maxChunkBytes = uploadsConfig.Value.MaxChunkBytes;
        long maxTaskBytes = uploadsConfig.Value.MaxTaskBytes;

        await foreach (
            CbzFileChunk request in requestStream.ReadAllAsync(context.CancellationToken)
        )
        {
            if (
                request.ChunkNumber < 0
                || request.ChunkNumber >= maxTotalChunks
                || request.TotalChunks > maxTotalChunks
            )
            {
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"Chunk number {request.ChunkNumber} or declared total {request.TotalChunks} exceeds the limit of {maxTotalChunks} chunks."
                    )
                );
            }

            if (request.Chunk.Length > maxChunkBytes)
            {
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"Chunk {request.ChunkNumber} is {request.Chunk.Length} bytes, exceeding the limit of {maxChunkBytes} bytes."
                    )
                );
            }

            if (!taskUploads.TryGetValue(request.TaskId, out TaskUploadState? state))
            {
                state = new TaskUploadState
                {
                    ContentId = string.IsNullOrEmpty(request.ContentId) ? null : request.ContentId,
                    Accepted = await IsAcceptingTaskAsync(
                        request.TaskId,
                        context.CancellationToken
                    ),
                };
                taskUploads[request.TaskId] = state;

                if (!state.Accepted)
                {
                    await responseStream.WriteAsync(
                        new UploadUpscaledCbzResponse
                        {
                            Success = false,
                            Message = "Task is unknown or in a terminal state.",
                            TaskId = request.TaskId,
                            Terminal = true,
                        }
                    );
                }
                else
                {
                    // Seed from the chunks already on disk so the byte cap covers the whole task,
                    // not just the chunks written in this call.
                    foreach (
                        var (chunkNumber, length) in await uploadStore.GetStoredChunkSizesAsync(
                            request.TaskId,
                            state.ContentId
                        )
                    )
                    {
                        state.ChunkSizes[chunkNumber] = length;
                        state.StoredBytes += length;
                    }
                }
            }
            else if (state.ContentId == null && !string.IsNullOrEmpty(request.ContentId))
            {
                // The identity arrived only after identity-less chunks. The next write wipes
                // everything stored for the task (including this call's chunks), so reset the
                // accounting to match.
                state.ContentId = request.ContentId;
                state.ChunkSizes.Clear();
                state.StoredBytes = 0;
            }

            if (!state.Accepted)
            {
                continue;
            }

            // Track the size stored for each chunk number so a re-sent chunk doesn't double-count,
            // and reject an upload that would push a single task past its disk bound.
            long previousSize = state.ChunkSizes.TryGetValue(request.ChunkNumber, out long size)
                ? size
                : 0;
            long projectedBytes = state.StoredBytes - previousSize + request.Chunk.Length;
            if (projectedBytes > maxTaskBytes)
            {
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"Task {request.TaskId} would exceed the {maxTaskBytes} byte upload limit."
                    )
                );
            }

            await uploadStore.WriteChunkAsync(
                request.TaskId,
                request.ChunkNumber,
                request.Chunk.ToByteArray(),
                state.ContentId,
                context.CancellationToken
            );

            state.ChunkSizes[request.ChunkNumber] = request.Chunk.Length;
            state.StoredBytes = projectedBytes;
            state.HighestChunkNumber = Math.Max(state.HighestChunkNumber, request.ChunkNumber);
            state.TotalChunks = Math.Max(state.TotalChunks, request.TotalChunks);
        }

        foreach (var (taskId, state) in taskUploads)
        {
            if (!state.Accepted)
            {
                continue;
            }

            // Older clients don't report the total, so infer it from the highest chunk received.
            int totalChunks =
                state.TotalChunks > 0 ? state.TotalChunks : state.HighestChunkNumber + 1;
            int receivedChunks = await uploadStore.GetContiguousChunkCountAsync(
                taskId,
                state.ContentId
            );

            if (receivedChunks < totalChunks)
            {
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = false,
                        Message =
                            $"Upload incomplete: {receivedChunks}/{totalChunks} chunks received.",
                        TaskId = taskId,
                    }
                );
                continue;
            }

            await AssembleAndProcessUploadAsync(
                taskId,
                totalChunks,
                state.ContentId,
                responseStream,
                context
            );
        }

        context.Status = new Status(StatusCode.OK, "File(s) uploaded");
    }

    public override async Task<UploadProgressResponse> GetUploadProgress(
        UploadProgressRequest request,
        ServerCallContext context
    )
    {
        PersistedTask? task = await dbContext
            .PersistedTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId, context.CancellationToken);

        if (
            task == null
            || task.Status is PersistedTaskStatus.Canceled or PersistedTaskStatus.Failed
        )
        {
            return new UploadProgressResponse { State = UploadState.Rejected };
        }

        if (task.Status == PersistedTaskStatus.Completed)
        {
            // The upload already committed; its chunks are no longer useful.
            await uploadStore.DeleteAsync(request.TaskId);
            return new UploadProgressResponse { State = UploadState.Complete };
        }

        return new UploadProgressResponse
        {
            State = UploadState.Pending,
            UploadedChunks = await uploadStore.GetContiguousChunkCountAsync(
                request.TaskId,
                request.ContentId
            ),
        };
    }

    /// <summary>
    /// Assembles the stored chunks into the temporary cbz, verifies the result against the client's
    /// declared content identity when one was supplied, and hands it to the task-specific
    /// processing. The per-task gate keeps a fast client retry from assembling and completing the
    /// same task twice.
    /// </summary>
    private async Task AssembleAndProcessUploadAsync(
        int taskId,
        int totalChunks,
        string? contentId,
        IServerStreamWriter<UploadUpscaledCbzResponse> responseStream,
        ServerCallContext context
    )
    {
        int gateIndex = (int)((uint)taskId % UploadProcessingLockCount);
        SemaphoreSlim gate = uploadProcessingGates[gateIndex];
        await gate.WaitAsync(context.CancellationToken);

        string? tempFile = null;
        IncrementalHash? hasher = string.IsNullOrEmpty(contentId)
            ? null
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            tempFile = PrepareTempFile(taskId);

            // The upload may have completed while a retry was in flight; don't process twice.
            PersistedTaskStatus? currentStatus = await dbContext
                .PersistedTasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => (PersistedTaskStatus?)t.Status)
                .FirstOrDefaultAsync(context.CancellationToken);

            if (currentStatus == PersistedTaskStatus.Completed)
            {
                await uploadStore.DeleteAsync(taskId);
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = true,
                        Message = "Chapter already upscaled",
                        TaskId = taskId,
                    }
                );
                return;
            }

            try
            {
                await using (FileStream fileStream = File.Create(tempFile))
                {
                    await uploadStore.AssembleAsync(
                        taskId,
                        totalChunks,
                        fileStream,
                        hasher,
                        context.CancellationToken
                    );
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A chunk disappeared between the completeness check and assembly (a concurrent
                // identity reset, ReportTaskFailed, or sweep). That is a resumable condition, not a
                // permanent failure: keep the chunks and ask the client to retry.
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = false,
                        Message = "Uploaded chunks changed during assembly; please retry.",
                        TaskId = taskId,
                    }
                );
                return;
            }

            // Guard against stale chunks from a different output being mixed with this one (the
            // store is keyed by identity, but a failed reset or concurrent writer could slip
            // through). A mismatch is retryable: the client re-uploads from scratch.
            if (
                hasher != null
                && !string.Equals(
                    ContentIdentity.FromSha256(hasher.GetHashAndReset()),
                    contentId,
                    StringComparison.Ordinal
                )
            )
            {
                await uploadStore.DeleteAsync(taskId);
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = false,
                        Message =
                            "Uploaded content did not match its declared identity; please retry.",
                        TaskId = taskId,
                    }
                );
                return;
            }

            await ProcessUploadedCbzAsync(taskId, tempFile, responseStream, context);
            await uploadStore.DeleteAsync(taskId);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client's connection dropped (deadline or socket). Keep the stored chunks and leave
            // the task processing so the worker can resume instead of re-uploading everything.
            throw;
        }
        catch (Exception ex) when (IsTransientProcessingFailure(ex))
        {
            // The chunks are already on disk and the failure is plausibly transient (a disk or
            // database blip during assembly/move/save), so keep them and ask the worker to retry
            // rather than forcing a full re-upload. The worker's retry budget bounds this, and if it
            // gives up it reports the task failed, which clears the chunks. The task intentionally
            // stays processing so GetUploadProgress keeps accepting the resume.
            _logger.LogWarning(
                ex,
                "Transient failure while processing the upload for task {TaskId}; keeping chunks for a retry.",
                taskId
            );
            await responseStream.WriteAsync(
                new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TaskId = taskId,
                }
            );
        }
        catch (Exception ex)
        {
            await uploadStore.DeleteAsync(taskId);

            // The task may have committed before a late response-write failure. A completed task
            // must not be reported (or logged) as failed.
            bool alreadyCompleted = false;
            try
            {
                // Use a non-cancelled token: the trigger is often a cancelled call, and the guard
                // is useless if the query is cancelled too.
                alreadyCompleted = await dbContext
                    .PersistedTasks.AsNoTracking()
                    .AnyAsync(
                        t => t.Id == taskId && t.Status == PersistedTaskStatus.Completed,
                        CancellationToken.None
                    );
            }
            catch (Exception)
            {
                // If the status cannot be read, fall through and report the failure.
            }

            if (alreadyCompleted)
            {
                await responseStream.WriteAsync(
                    new UploadUpscaledCbzResponse
                    {
                        Success = true,
                        Message = "Chapter already upscaled",
                        TaskId = taskId,
                    }
                );
                return;
            }

            // Ensure the task is marked as failed so it doesn't get stuck in Processing. The task is
            // now Failed, so the worker must not keep retrying this upload.
            await taskProcessor.TaskFailed(taskId, ex.Message);

            await responseStream.WriteAsync(
                new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TaskId = taskId,
                    Terminal = true,
                }
            );
        }
        finally
        {
            hasher?.Dispose();
            SafeDeleteFile(tempFile);
            gate.Release();
        }
    }

    private async Task ProcessUploadedCbzAsync(
        int taskId,
        string tempFile,
        IServerStreamWriter<UploadUpscaledCbzResponse> responseStream,
        ServerCallContext context
    )
    {
        PersistedTask? task = await dbContext.PersistedTasks.FindAsync(taskId);
        if (task == null || task.Status == PersistedTaskStatus.Canceled)
        {
            File.Delete(tempFile);
            await responseStream.WriteAsync(
                new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Task not found or cancelled",
                    TaskId = taskId,
                    Terminal = true,
                }
            );
            return;
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
                        Terminal = true,
                    }
                );
                return;
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
                        Terminal = true,
                    }
                );
                return;
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
        else if (task.Data is RepairUpscaleTask)
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
                        Message = "No upscaled missing pages path specified for repair task.",
                        TaskId = taskId,
                        Terminal = true,
                    }
                );
                return;
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
                        .ThenInclude(l => l.UpscalerProfile)
                .Include(chapter => chapter.Manga)
                    .ThenInclude(manga => manga.UpscalerProfilePreference)
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
                        Terminal = true,
                    }
                );
                return;
            }

            // Replace original file
            if (File.Exists(chapter.NotUpscaledFullPath))
            {
                File.Delete(chapter.NotUpscaledFullPath);
            }

            fileSystem.Move(tempFile, chapter.NotUpscaledFullPath);

            await splitProcessingCoordinator.OnSplitsAppliedAsync(
                applySplitsTask.ChapterId,
                applySplitsTask.DetectorVersion
            );

            await taskProcessor.TaskCompleted(taskId);
            await responseStream.WriteAsync(
                new UploadUpscaledCbzResponse
                {
                    Success = true,
                    Message = "Splits applied",
                    TaskId = taskId,
                }
            );
        }
        else
        {
            await responseStream.WriteAsync(
                new UploadUpscaledCbzResponse
                {
                    Success = false,
                    Message = "Invalid task type",
                    TaskId = taskId,
                    Terminal = true,
                }
            );
            return;
        }
    }

    public override async Task<Empty> ReportTaskFailed(
        ReportTaskFailedRequest request,
        ServerCallContext context
    )
    {
        // Mark the task failed first. If that throws, the stored chunks survive to be resumed or
        // swept; deleting them first would lose the resumable state while leaving the task as it was.
        await taskProcessor.TaskFailed(request.TaskId, request.ErrorMessage);
        await uploadStore.DeleteAsync(request.TaskId);
        return new Empty();
    }

    private string PrepareTempFile(int taskId)
    {
        fileSystem.CreateDirectory(tempDir);
        // The fixed per-task name is safe because assembly for a task is serialized by the
        // processing gate; revisit this if that serialization ever goes away.
        return Path.Combine(tempDir, $"upscaled_{taskId}.cbz");
    }

    /// <summary>
    /// Whether a processing failure is plausibly transient, so the stored chunks should be kept and
    /// the worker allowed to resume: disk/transport I/O and database failures, as opposed to logic
    /// errors that would deterministically recur and warrant a terminal failure.
    /// </summary>
    private static bool IsTransientProcessingFailure(Exception exception) =>
        exception is IOException or DbException or DbUpdateException;

    private static void SafeDeleteFile(string? path)
    {
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
    /// Whether the server is willing to store chunks for a task. Mirrors
    /// <see cref="GetUploadProgress"/>'s rejection rule (unknown, cancelled or failed tasks);
    /// completed tasks are accepted so the assembly path can resolve them as a no-op. Without this
    /// a client could create unbounded <c>task_N</c> directories for arbitrary ids.
    /// </summary>
    private async Task<bool> IsAcceptingTaskAsync(int taskId, CancellationToken cancellationToken)
    {
        PersistedTaskStatus? status = await dbContext
            .PersistedTasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (PersistedTaskStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status.HasValue
            && status.Value != PersistedTaskStatus.Canceled
            && status.Value != PersistedTaskStatus.Failed;
    }

    private sealed class TaskUploadState
    {
        public int HighestChunkNumber { get; set; } = -1;
        public int TotalChunks { get; set; }
        public string? ContentId { get; set; }
        public bool Accepted { get; set; } = true;
        public Dictionary<int, long> ChunkSizes { get; } = new();
        public long StoredBytes { get; set; }
    }
}
