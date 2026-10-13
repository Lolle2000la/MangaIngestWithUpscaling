using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// The claim RPC's fallback for a server from before the hint-bearing RPC existed: the claim loop
/// would otherwise re-issue the rejected call roughly every 500 ms forever without ever claiming a
/// task from such a server.
/// </summary>
public class GrpcTaskClaimSourceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnimplementedHintRpc_FallsBackToTheUnhintedClaim()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .RequestUpscaleTaskWithHintAsync(
                Arg.Any<RequestTaskRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new RpcException(new Status(StatusCode.Unimplemented, "no such RPC")));
        client
            .RequestUpscaleTaskAsync(
                Arg.Any<Empty>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Unary(new UpscaleTaskDelegationResponse { TaskId = 7 }));

        var source = new GrpcTaskClaimSource(client);

        // The first claim eats one Unimplemented round-trip and then claims through the un-hinted
        // RPC the interface documents as the fallback for older servers.
        UpscaleTaskDelegationResponse first = await source.RequestTaskWithHintAsync(
            CancellationToken.None
        );
        Assert.Equal(7, first.TaskId);

        // The skew is latched: later claims skip the doomed hint call instead of paying for the
        // rejection again on every one of them. The hint is an optimization, so a server upgraded
        // mid-process only picks it back up on the next worker start.
        UpscaleTaskDelegationResponse second = await source.RequestTaskWithHintAsync(
            CancellationToken.None
        );
        Assert.Equal(7, second.TaskId);

        _ = client
            .Received(1)
            .RequestUpscaleTaskWithHintAsync(
                Arg.Any<RequestTaskRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            );
        _ = client
            .Received(2)
            .RequestUpscaleTaskAsync(
                Arg.Any<Empty>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            );
    }

    private static AsyncUnaryCall<T> Unary<T>(T value) =>
        new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }
        );
}
