using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Behavioural coverage for the worker half of quiet hours: the fetch loop must issue no claim while
/// the worker's own window is open, and must start claiming once it closes. Driven through the real
/// <see cref="RemoteTaskProcessor" /> loop against a stubbed claim source, so no gRPC server is needed.
/// </summary>
public class RemoteTaskProcessorQuietHoursTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchLoop_IssuesNoClaimWhileQuietHoursAreActive()
    {
        var claimSource = new CountingTaskClaimSource();
        RemoteTaskProcessor processor = CreateProcessor(
            claimSource,
            GateCoveringNowFor(TimeSpan.FromHours(2), TimeSpan.FromHours(2))
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await processor.StartAsync(cts.Token);
        try
        {
            await Task.Delay(600, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref claimSource.Claims));
        }
        finally
        {
            await cts.CancelAsync();
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchLoop_ClaimsOnceTheWindowEnds()
    {
        // The window closes about two seconds from now: the loop must be blocked on it at first, then
        // claim without any other trigger.
        var claimSource = new CountingTaskClaimSource();
        RemoteTaskProcessor processor = CreateProcessor(
            claimSource,
            GateCoveringNowFor(TimeSpan.FromHours(1), TimeSpan.FromSeconds(2))
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        try
        {
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref claimSource.Claims));

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (Volatile.Read(ref claimSource.Claims) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(Volatile.Read(ref claimSource.Claims) >= 1);
        }
        finally
        {
            await cts.CancelAsync();
            await processor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A gate whose window covers "now", opening <paramref name="before" /> ago.</summary>
    private static IQuietHoursGate GateCoveringNowFor(TimeSpan before, TimeSpan after)
    {
        TimeSpan now = TimeProvider.System.GetLocalNow().TimeOfDay;
        return new QuietHoursGate(
            Options.Create(
                new QuietHoursConfig
                {
                    Enabled = true,
                    Start = WrapAroundDay(now - before),
                    End = WrapAroundDay(now + after),
                }
            ),
            TimeProvider.System
        );
    }

    private static TimeSpan WrapAroundDay(TimeSpan value)
    {
        long ticksPerDay = TimeSpan.FromDays(1).Ticks;
        long ticks = value.Ticks % ticksPerDay;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + ticksPerDay : ticks);
    }

    private static RemoteTaskProcessor CreateProcessor(
        ITaskClaimSource claimSource,
        IQuietHoursGate gate
    )
    {
        var provider = new ServiceCollection()
            .AddSingleton(CreateServer())
            .AddSingleton(claimSource)
            .AddSingleton(CreatePageStreamClient())
            .AddSingleton(Substitute.For<ILogger<RemoteTaskProcessor>>())
            .AddSingleton(gate)
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        return new RemoteTaskProcessor(provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>Counts claim requests; every claim returns the same task so the loop keeps cycling.</summary>
    private sealed class CountingTaskClaimSource : ITaskClaimSource
    {
        public int Claims;

        public Task<UpscaleTaskDelegationResponse> RequestTaskWithHintAsync(
            CancellationToken stoppingToken
        )
        {
            Interlocked.Increment(ref Claims);
            return Task.FromResult(
                new UpscaleTaskDelegationResponse { TaskId = 1, TaskType = TaskType.Upscale }
            );
        }
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

    /// <summary>
    /// A server whose manifest fails deterministically, so a claim that does happen never leaves the
    /// loop waiting on the streaming protocol.
    /// </summary>
    private static UpscalingService.UpscalingServiceClient CreateServer()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .GetPageManifestAsync(
                Arg.Any<PageManifestRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => throw new RpcException(new Status(StatusCode.InvalidArgument, "bad")));
        client
            .ReportTaskFailedAsync(
                Arg.Any<ReportTaskFailedRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Unary(new Empty()));
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
