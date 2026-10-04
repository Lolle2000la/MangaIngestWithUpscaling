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
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.MetadataHandling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
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
    IPageSpoolStore pageStreamSpool,
    PageContextCache pageContextCache,
    IUpscalerJsonHandlingService upscalerJsonHandlingService,
    IMetadataHandlingService metadataHandling,
    IImageResizeService imageResizeService,
    IOptions<UpscalerConfig> upscalerConfig,
    ILogger<UpscalingDistributionService> logger
) : UpscalingService.UpscalingServiceBase
{
    private readonly ILogger<UpscalingDistributionService> _logger = logger;
    private readonly UpscalerConfig _upscalerConfig = upscalerConfig.Value;
    private readonly PageStreamFinalizer _pageStreamFinalizer = new(pageStreamSpool, logger);

    public override Task<CheckConnectionResponse> CheckConnection(
        CheckConnectionRequest request,
        ServerCallContext context
    )
    {
        // Validate the worker's version against this server's range. An old worker that still sends
        // the former Empty request arrives as version 0, so it is rejected here with a clear message
        // instead of failing with an opaque Unimplemented on every removed RPC.
        if (
            !UpscalingProtocolVersion.IsCompatible(
                request.ProtocolVersion,
                request.MinSupportedProtocolVersion
            )
        )
        {
            _logger.LogWarning(
                "Rejecting a worker speaking upscaling protocol version {WorkerVersion} (range {WorkerMin}-{WorkerMax}; this server supports {Min}-{Max}).",
                request.ProtocolVersion,
                request.MinSupportedProtocolVersion,
                request.ProtocolVersion,
                UpscalingProtocolVersion.MinSupported,
                UpscalingProtocolVersion.Current
            );
            context.Status = new Status(
                StatusCode.FailedPrecondition,
                $"Incompatible upscaling protocol version {request.ProtocolVersion}; this server supports {UpscalingProtocolVersion.MinSupported}-{UpscalingProtocolVersion.Current}. Upgrade the worker and server together."
            );
            return Task.FromResult(
                new CheckConnectionResponse
                {
                    Message = "Incompatible upscaling protocol version",
                    Success = false,
                    ProtocolVersion = UpscalingProtocolVersion.Current,
                    MinSupportedProtocolVersion = UpscalingProtocolVersion.MinSupported,
                }
            );
        }

        context.Status = new Status(StatusCode.OK, "Connection established");
        return Task.FromResult(
            new CheckConnectionResponse
            {
                Message = "Connection established",
                Success = true,
                ProtocolVersion = UpscalingProtocolVersion.Current,
                MinSupportedProtocolVersion = UpscalingProtocolVersion.MinSupported,
            }
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
            UpscalerProfile = ToProtoProfile(upscalerProfile),
        };

        return response;
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
