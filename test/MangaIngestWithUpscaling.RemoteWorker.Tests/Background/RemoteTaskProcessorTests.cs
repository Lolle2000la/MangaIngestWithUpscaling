using Grpc.Core;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

public class RemoteTaskProcessorTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(StatusCode.Unavailable, RemoteTaskProcessor.StreamingFailureKind.Transient)]
    [InlineData(StatusCode.DeadlineExceeded, RemoteTaskProcessor.StreamingFailureKind.Transient)]
    [InlineData(StatusCode.Unimplemented, RemoteTaskProcessor.StreamingFailureKind.Permanent)]
    [InlineData(StatusCode.Internal, RemoteTaskProcessor.StreamingFailureKind.Permanent)]
    [InlineData(StatusCode.InvalidArgument, RemoteTaskProcessor.StreamingFailureKind.Permanent)]
    public void ClassifyStreamingFailure_ClassifiesRpcStatusCodes(
        StatusCode code,
        RemoteTaskProcessor.StreamingFailureKind expected
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
            RemoteTaskProcessor.StreamingFailureKind.Transient,
            RemoteTaskProcessor.ClassifyStreamingFailure(wrapped)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsANonTerminalRejectionAsRestart()
    {
        Assert.Equal(
            RemoteTaskProcessor.StreamingFailureKind.Restart,
            RemoteTaskProcessor.ClassifyStreamingFailure(new PageStreamRestartException("restart"))
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassifyStreamingFailure_TreatsAnOrdinaryErrorAsPermanent()
    {
        Assert.Equal(
            RemoteTaskProcessor.StreamingFailureKind.Permanent,
            RemoteTaskProcessor.ClassifyStreamingFailure(
                new InvalidOperationException("The server did not send page 0.")
            )
        );
    }
}
