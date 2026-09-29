using System.Net.Sockets;
using Grpc.Core;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

public class UploadRetryPolicyTests
{
    private static WorkerConfig Config() =>
        new()
        {
            UploadRetryBaseDelay = TimeSpan.FromSeconds(5),
            UploadTimeoutFloor = TimeSpan.FromMinutes(2),
            UploadMinThroughputBytesPerSecond = 128 * 1024,
        };

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeAttemptTimeout_UsesFloorForSmallRemainders()
    {
        TimeSpan timeout = UploadRetryPolicy.ComputeAttemptTimeout(0, Config());

        Assert.Equal(TimeSpan.FromMinutes(2), timeout);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeAttemptTimeout_ScalesWithRemainingBytes()
    {
        WorkerConfig config = Config();
        // 10 minutes worth of bytes at the configured throughput.
        long remainingBytes = (long)config.UploadMinThroughputBytesPerSecond * 600;

        TimeSpan timeout = UploadRetryPolicy.ComputeAttemptTimeout(remainingBytes, config);

        Assert.Equal(TimeSpan.FromMinutes(10), timeout);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeAttemptTimeout_IsCapped()
    {
        WorkerConfig config = Config();
        config.UploadMinThroughputBytesPerSecond = 1;

        TimeSpan timeout = UploadRetryPolicy.ComputeAttemptTimeout(1_000_000_000, config);

        Assert.Equal(TimeSpan.FromHours(6), timeout);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetRetryDelay_BacksOffExponentially()
    {
        WorkerConfig config = Config();

        Assert.Equal(TimeSpan.FromSeconds(5), UploadRetryPolicy.GetRetryDelay(1, config));
        Assert.Equal(TimeSpan.FromSeconds(10), UploadRetryPolicy.GetRetryDelay(2, config));
        Assert.Equal(TimeSpan.FromSeconds(20), UploadRetryPolicy.GetRetryDelay(3, config));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetRetryDelay_IsCapped()
    {
        TimeSpan delay = UploadRetryPolicy.GetRetryDelay(20, Config());

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanRetry_TrueWhileBothBudgetsRemain()
    {
        WorkerConfig config = Config();

        Assert.True(UploadRetryPolicy.CanRetry(attempt: 1, TimeSpan.Zero, config));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanRetry_FalseWhenAttemptsExhausted()
    {
        WorkerConfig config = Config();

        Assert.False(UploadRetryPolicy.CanRetry(config.UploadMaxAttempts, TimeSpan.Zero, config));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanRetry_FalseWhenElapsedBudgetExhausted()
    {
        WorkerConfig config = Config();

        // Few attempts used, but the outage has lasted longer than the configured budget.
        Assert.False(UploadRetryPolicy.CanRetry(1, config.UploadRetryMaxElapsed, config));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.ResourceExhausted)]
    [InlineData(StatusCode.Aborted)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.Unknown)]
    public void IsRetryable_TransportRpcStatusesAreRetryable(StatusCode statusCode)
    {
        Assert.True(UploadRetryPolicy.IsRetryable(new RpcException(new Status(statusCode, ""))));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsRetryable_FileAndSocketErrorsAreRetryable()
    {
        Assert.True(UploadRetryPolicy.IsRetryable(new IOException("broken")));
        Assert.True(UploadRetryPolicy.IsRetryable(new SocketException()));
        Assert.True(UploadRetryPolicy.IsRetryable(new RetryableUploadException("incomplete")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsRetryable_PermanentFailuresAreNotRetryable()
    {
        Assert.False(UploadRetryPolicy.IsRetryable(new NonRetryableUploadException("rejected")));
        Assert.False(UploadRetryPolicy.IsRetryable(new FileNotFoundException("gone")));
        Assert.False(UploadRetryPolicy.IsRetryable(new DirectoryNotFoundException("gone")));
        Assert.False(UploadRetryPolicy.IsRetryable(new InvalidOperationException("nope")));
    }
}
