using System.Text;
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
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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
    PageStreamSpool pageStreamSpool,
    PageContextCache pageContextCache,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IMetadataHandlingService metadataHandling,
    IImageResizeService imageResizeService,
    ILogger<UpscalingDistributionService> logger
) : UpscalingService.UpscalingServiceBase
{
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

    public override async Task<Empty> ReportTaskFailed(
        ReportTaskFailedRequest request,
        ServerCallContext context
    )
    {
        await taskProcessor.TaskFailed(request.TaskId, request.ErrorMessage);
        return new Empty();
    }
}
