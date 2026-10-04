using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

public class RemoteTaskProcessorTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(StatusCode.Unavailable, StreamingFailureKind.Transient)]
    [InlineData(StatusCode.DeadlineExceeded, StreamingFailureKind.Transient)]
    [InlineData(StatusCode.Cancelled, StreamingFailureKind.Transient)]
    [InlineData(StatusCode.FailedPrecondition, StreamingFailureKind.Restart)]
    [InlineData(StatusCode.Unimplemented, StreamingFailureKind.Transient)]
    [InlineData(StatusCode.Internal, StreamingFailureKind.Permanent)]
    [InlineData(StatusCode.InvalidArgument, StreamingFailureKind.Permanent)]
    public void ClassifyStreamingFailure_ClassifiesRpcStatusCodes(
        StatusCode code,
        StreamingFailureKind expected
    )
    {
        Assert.Equal(
            expected,
            RemoteTaskProcessor.ClassifyStreamingFailure(new RpcException(new Status(code, "test")))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_UnwrapsTheProducerWrapper()
    {
        // RunChapterAsync wraps a producer failure; a transient fetch failure must still be
        // classified as transient, or a network blip would delete the spool and burn a retry.
        var wrapped = new InvalidOperationException(
            "Failed to stream the chapter pages to the upscale worker.",
            new RpcException(new Status(StatusCode.Unavailable, "blip"))
        );

        Assert.Equal(
            StreamingFailureKind.Transient,
            RemoteTaskProcessor.ClassifyStreamingFailure(wrapped)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsANonTerminalRejectionAsRestart()
    {
        Assert.Equal(
            StreamingFailureKind.Restart,
            RemoteTaskProcessor.ClassifyStreamingFailure(new PageStreamRestartException("restart"))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsAnOrdinaryErrorAsPermanent()
    {
        Assert.Equal(
            StreamingFailureKind.Permanent,
            RemoteTaskProcessor.ClassifyStreamingFailure(
                new InvalidOperationException("The server did not send page 0.")
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsAWorkerCrashAsTransient()
    {
        // A crashed worker (OOM/CUDA fault) must not drop the already-spooled pages: respawn and
        // resume instead of reporting a permanent failure.
        Assert.Equal(
            StreamingFailureKind.Transient,
            RemoteTaskProcessor.ClassifyStreamingFailure(
                new UpscaleWorkerCrashedException("Upscale worker process exited unexpectedly.")
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_DoesNotReportATransientFailure()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();

        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            1,
            new RpcException(new Status(StatusCode.Unavailable, "blip")),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        // Reporting a failure makes the server delete the spool; a transient failure must not.
        AssertNoFailureReported(client);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_DoesNotReportARestartFailure()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();

        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            1,
            new PageStreamRestartException("restart"),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        AssertNoFailureReported(client);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_ReportsAPermanentFailure()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new AsyncUnaryCall<Empty>(
                    Task.FromResult(new Empty()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => new Metadata(),
                    () => { }
                )
            );

        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            7,
            new InvalidOperationException("deterministic"),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        _ = client
            .Received(1)
            .ReportTaskFailedAsync(
                Arg.Is<ReportTaskFailedRequest>(r => r.TaskId == 7),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_ReportsFalseWhenTheReportRpcFails()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<AsyncUnaryCall<Empty>>(_ =>
                throw new RpcException(new Status(StatusCode.Unavailable, "report failed"))
            );

        // The report did not land, so the task was not terminalized: the caller must keep the
        // soft-failure counter, or a deterministic failure whose report keeps failing would reset it
        // and cycle forever.
        bool reported = await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            7,
            new InvalidOperationException("deterministic"),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        Assert.False(reported);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsAWorkerTimeoutAsTransient()
    {
        // A wedged worker (a cold model load or a CUDA stall past the scaled inactivity timeout) is
        // as recoverable as a crash: respawn and resume rather than discarding the spool.
        Assert.Equal(
            StreamingFailureKind.Transient,
            RemoteTaskProcessor.ClassifyStreamingFailure(new TimeoutException("inactivity"))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_ReportsAfterTheConsecutiveSoftFailureCap()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new AsyncUnaryCall<Empty>(
                    Task.FromResult(new Empty()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => new Metadata(),
                    () => { }
                )
            );

        // A soft failure that keeps recurring is deterministic in practice; past the cap it must be
        // reported, or the server's dead-task reaper would requeue it forever without a budget.
        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            9,
            new RpcException(new Status(StatusCode.Unavailable, "blip")),
            Substitute.For<ILogger>(),
            CancellationToken.None,
            consecutiveSoftFailures: 1000
        );

        _ = client
            .Received(1)
            .ReportTaskFailedAsync(
                Arg.Is<ReportTaskFailedRequest>(r => r.TaskId == 9),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_DoesNotReportARestartBelowTheRestartCap()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();

        // A restart (no spool on this replica / identity changed) can recur under a misconfiguration;
        // it must not be terminalised at the small soft-failure cap.
        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            11,
            new PageStreamRestartException("no spool on this replica"),
            Substitute.For<ILogger>(),
            CancellationToken.None,
            consecutiveSoftFailures: 6
        );

        AssertNoFailureReported(client);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_ReadsTheRpcStatusFromTheInnerException()
    {
        // grpc-dotnet keeps the transport error in InnerException, so GetBaseException() returns the
        // SocketException/HttpRequestException and misses the RpcException. The classifier must walk
        // the chain, or a routine Unavailable would be reported as Permanent and delete the spool.
        var ex = new RpcException(
            new Status(StatusCode.Unavailable, "blip", new HttpRequestException("connection down"))
        );

        Assert.Equal(
            StreamingFailureKind.Transient,
            RemoteTaskProcessor.ClassifyStreamingFailure(ex)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_ReportsAnIncompatibleServerVersionImmediately()
    {
        // Unimplemented means the server does not know the RPC, i.e. the pair is across the upgrade
        // cutover. A running worker only validated the protocol at startup, so this asks again and, when
        // the versions really cannot work together, reports the task with the cause instead of cycling it
        // through the soft-failure cap (which would fail other chapters in turn).
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        StubCheckConnection(
            client,
            new CheckConnectionResponse
            {
                ProtocolVersion = UpscalingProtocolVersion.Current + 1,
                MinSupportedProtocolVersion = UpscalingProtocolVersion.Current + 1,
            }
        );
        StubReportTaskFailed(client);

        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            11,
            new RpcException(new Status(StatusCode.Unimplemented, "unknown method")),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        _ = client
            .Received(1)
            .ReportTaskFailedAsync(
                Arg.Is<ReportTaskFailedRequest>(r =>
                    r.TaskId == 11 && r.ErrorMessage.Contains("protocol")
                ),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleStreamingFailure_KeepsRetryingWhenTheServerVersionIsCompatible()
    {
        // A server mid-rollout can answer the handshake within range, so the missing RPC is a rollout
        // condition: keep the spool and retry rather than failing the chapter.
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        StubCheckConnection(
            client,
            new CheckConnectionResponse
            {
                ProtocolVersion = UpscalingProtocolVersion.Current,
                MinSupportedProtocolVersion = UpscalingProtocolVersion.MinSupported,
            }
        );

        await RemoteTaskProcessor.HandleStreamingFailureAsync(
            client,
            12,
            new RpcException(new Status(StatusCode.Unimplemented, "unknown method")),
            Substitute.For<ILogger>(),
            CancellationToken.None
        );

        AssertNoFailureReported(client);
    }

    private static void StubCheckConnection(
        UpscalingService.UpscalingServiceClient client,
        CheckConnectionResponse response
    ) =>
        client
            .CheckConnectionAsync(
                Arg.Any<CheckConnectionRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Unary(response));

    private static void StubReportTaskFailed(UpscalingService.UpscalingServiceClient client) =>
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Unary(new Empty()));

    private static AsyncUnaryCall<T> Unary<T>(T response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }
        );

    private static void AssertNoFailureReported(UpscalingService.UpscalingServiceClient client) =>
        Assert.DoesNotContain(
            client.ReceivedCalls(),
            call =>
                call.GetMethodInfo().Name
                == nameof(UpscalingService.UpscalingServiceClient.ReportTaskFailedAsync)
        );
}
