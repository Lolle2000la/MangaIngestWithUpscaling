using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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
/// <see cref="ITaskClaimSource" /> over the generated gRPC client. The loop prefers the hint-bearing
/// RPC and falls back to the un-hinted <c>RequestUpscaleTask</c> for servers from before it existed —
/// without the fallback the claim loop would spin on <c>Unimplemented</c> roughly every 500 ms
/// without ever claiming a task from such a server.
/// </summary>
public sealed class GrpcTaskClaimSource(UpscalingService.UpscalingServiceClient client)
    : ITaskClaimSource
{
    /// <summary>
    /// Process-level, not per instance: the claim source is registered transient and the claim loop
    /// resolves it from a new DI scope every iteration, so an instance field reset itself before it
    /// could ever help — the doomed hint RPC was paid again on every fetch. This is a fact about the
    /// server the worker is talking to, so its lifetime is the worker process. Volatile because any
    /// thread that resolves a claim source may read it.
    /// </summary>
    private static volatile bool serverLacksHintRpc;

    public async Task<UpscaleTaskDelegationResponse> RequestTaskWithHintAsync(
        CancellationToken stoppingToken
    )
    {
        if (!serverLacksHintRpc)
        {
            try
            {
                return await client.RequestUpscaleTaskWithHintAsync(
                    new RequestTaskRequest { Prefetch = true },
                    deadline: DateTime.UtcNow.AddSeconds(15),
                    cancellationToken: stoppingToken
                );
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // A server from before the hint RPC. The claim loop's own retry would just hit
                // the same rejection again, so the un-hinted RPC is used from now on instead of
                // only for this one call. Latching costs nothing for an upgraded server: the
                // hint is an optimization, and the next worker process picks it back up.
                serverLacksHintRpc = true;
            }
        }

        return await client.RequestUpscaleTaskAsync(
            new Empty(),
            deadline: DateTime.UtcNow.AddSeconds(15),
            cancellationToken: stoppingToken
        );
    }
}
