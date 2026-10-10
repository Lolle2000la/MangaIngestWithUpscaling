using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Drives the full <see cref="RemoteTaskProcessor.ExecuteAsync"/> claim → stream loop against a
/// stubbed server, so the consecutive-soft-failure counter and its cap are exercised end to end (not
/// just the extracted classifier). The streaming client's manifest call is the failure point, which
/// keeps the test independent of the upscaler and the page protocol.
/// </summary>
public class RemoteTaskProcessorLoopTests
{
    /// <summary>Mirrors <c>SoftFailureTracker.MaxConsecutiveSoftFailures</c>.</summary>
    private const int SoftFailureCap = SoftFailureTracker.MaxConsecutiveSoftFailures;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_ReportsATransientFailureOnlyAfterTheSoftFailureCap()
    {
        // A transient failure that keeps recurring is deterministic in practice. The loop must count
        // consecutive soft failures and report only after the cap, so a one-off blip keeps the spool
        // (no report) while a deterministically-bad task still surfaces instead of cycling forever.
        int manifestCalls = 0;
        var reported = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var client = CreateServer(
            manifestError: () =>
            {
                Interlocked.Increment(ref manifestCalls);
                return new RpcException(new Status(StatusCode.Unavailable, "blip"));
            },
            onReported: () => reported.TrySetResult(Volatile.Read(ref manifestCalls))
        );

        RemoteTaskProcessor processor = CreateProcessor(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        try
        {
            int callsAtReport = await reported.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(SoftFailureCap, callsAtReport);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_ReportsAPermanentFailureImmediately()
    {
        // A deterministic failure must not be retried to the cap: report it on the first attempt so a
        // bad task is surfaced promptly rather than requeued five times.
        int manifestCalls = 0;
        var reported = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var client = CreateServer(
            manifestError: () =>
            {
                Interlocked.Increment(ref manifestCalls);
                return new RpcException(new Status(StatusCode.InvalidArgument, "bad"));
            },
            onReported: () => reported.TrySetResult(Volatile.Read(ref manifestCalls))
        );

        RemoteTaskProcessor processor = CreateProcessor(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        try
        {
            int callsAtReport = await reported.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(1, callsAtReport);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private static RemoteTaskProcessor CreateProcessor(
        UpscalingService.UpscalingServiceClient client
    )
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton<ITaskClaimSource>(new FakeTaskClaimSource())
            .AddSingleton(CreatePageStreamClient())
            .AddSingleton(Substitute.For<ILogger<RemoteTaskProcessor>>())
            .BuildServiceProvider();
        return new RemoteTaskProcessor(provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Drives the claim seam without gRPC: every claim returns the same task, so the loop keeps
    /// failing it and the soft-failure counter is exercised.
    /// </summary>
    private sealed class FakeTaskClaimSource : ITaskClaimSource
    {
        public Task<UpscaleTaskDelegationResponse> RequestTaskWithHintAsync(
            CancellationToken stoppingToken
        ) =>
            Task.FromResult(
                new UpscaleTaskDelegationResponse { TaskId = 1, TaskType = TaskType.Upscale }
            );
    }

    private static PageStreamClient CreatePageStreamClient()
    {
        var provider = new ServiceCollection()
            .AddSingleton(Substitute.For<IImageResizeService>())
            .BuildServiceProvider();
        return new PageStreamClient(
            Substitute.For<IMangaJaNaiWorkerClient>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig { ImageFormatConversionRules = [] }),
            new StubEngineIdentityProvider(),
            Substitute.For<ILogger<PageStreamClient>>()
        );
    }

    private static UpscalingService.UpscalingServiceClient CreateServer(
        Func<RpcException> manifestError,
        Action onReported
    )
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .GetPageManifestAsync(
                Arg.Any<PageManifestRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => throw manifestError());
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                onReported();
                return Unary(new Empty());
            });
        client
            .KeepAliveAsync(
                Arg.Any<KeepAliveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Unary(new KeepAliveResponse { IsAlive = true }));
        return client;
    }

    private static AsyncUnaryCall<T> Unary<T>(T value) =>
        new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }
        );

    private sealed class StubEngineIdentityProvider : IEngineIdentityProvider
    {
        public string Upscaler => "test-engine";
        public string Detector => "test-engine";
    }
}
