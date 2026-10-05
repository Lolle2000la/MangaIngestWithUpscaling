using MangaIngestWithUpscaling.Api.Upscaling;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// The claim half of the distribution RPC, isolated so the task loop can be driven by a fake without
/// a live gRPC channel. The production implementation owns the prefetch hint and the call deadline.
/// </summary>
public interface ITaskClaimSource
{
    /// <summary>
    /// Claims the next task using the prefetch hint. A response with <c>TaskId == -1</c> means there
    /// is no work.
    /// </summary>
    Task<UpscaleTaskDelegationResponse> RequestTaskWithHintAsync(CancellationToken stoppingToken);
}

/// <summary>
/// <see cref="ITaskClaimSource" /> over the generated gRPC client. The loop only needs the
/// hint-bearing RPC; <c>RequestUpscaleTask</c> remains the un-hinted fallback for older servers.
/// </summary>
public sealed class GrpcTaskClaimSource(UpscalingService.UpscalingServiceClient client)
    : ITaskClaimSource
{
    public async Task<UpscaleTaskDelegationResponse> RequestTaskWithHintAsync(
        CancellationToken stoppingToken
    ) =>
        await client.RequestUpscaleTaskWithHintAsync(
            new RequestTaskRequest { Prefetch = true },
            deadline: DateTime.UtcNow.AddSeconds(15),
            cancellationToken: stoppingToken
        );
}
