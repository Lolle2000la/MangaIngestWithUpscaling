using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Pins how the keep-alive pump reports a task the server no longer tracks. The streaming loop turns
/// that signal into a clean stop rather than a soft failure (grpc-dotnet surfaces the resulting
/// stream cancellation as RpcException(Cancelled), which the classifier would otherwise count as
/// transient and could push toward the terminal cap).
/// </summary>
public class KeepAlivePumpTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NotFound_SignalsTheTaskIsGoneAndStops()
    {
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .KeepAliveAsync(
                Arg.Any<KeepAliveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<AsyncUnaryCall<KeepAliveResponse>>(_ =>
                throw new RpcException(
                    new Status(StatusCode.NotFound, "Task not found or cancelled")
                )
            );

        using var cts = new CancellationTokenSource();
        bool taskGone = false;
        Task run = CreatePump(client)
            .RunAsync(
                cts,
                () => 1,
                id => new KeepAliveRequest { TaskId = id },
                () => taskGone = true
            );

        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(taskGone, "A NotFound keep-alive must report the task as gone.");
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NotAliveResponse_SignalsTheTaskIsGoneAndStops()
    {
        // The current server signals a gone task by setting the RPC status to NotFound, which replaces
        // the response (see the test above). This is the defensive fallback for a server that reports
        // it with an OK status.
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .KeepAliveAsync(
                Arg.Any<KeepAliveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Unary(new KeepAliveResponse { IsAlive = false }));

        using var cts = new CancellationTokenSource();
        bool taskGone = false;
        Task run = CreatePump(client)
            .RunAsync(
                cts,
                () => 1,
                id => new KeepAliveRequest { TaskId = id },
                () => taskGone = true
            );

        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(taskGone);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_TransientFailure_DoesNotSignalTheTaskIsGone()
    {
        // A transport blip is not "the task is gone": the pump keeps running and must not signal a
        // stop, so the streaming loop still counts the failure as transient.
        int calls = 0;
        var firstCall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
        client
            .KeepAliveAsync(
                Arg.Any<KeepAliveRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<AsyncUnaryCall<KeepAliveResponse>>(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstCall.TrySetResult();
                    throw new RpcException(new Status(StatusCode.Unavailable, "blip"));
                }

                return Unary(new KeepAliveResponse { IsAlive = true });
            });

        using var cts = new CancellationTokenSource();
        bool taskGone = false;
        Task run = CreatePump(client)
            .RunAsync(
                cts,
                () => 1,
                id => new KeepAliveRequest { TaskId = id },
                () => taskGone = true
            );

        await firstCall.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        Assert.False(taskGone);
        Assert.False(cts.IsCancellationRequested);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static KeepAlivePump CreatePump(UpscalingService.UpscalingServiceClient client)
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton<ILogger<RemoteTaskProcessor>>(NullLogger<RemoteTaskProcessor>.Instance)
            .BuildServiceProvider();
        return new KeepAlivePump(provider.GetRequiredService<IServiceScopeFactory>());
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
