using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

public class ApplySplitsTaskTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessAsync_WhenApplyFails_SetsTheStateFailed()
    {
        // Regression guard: a failed apply left the chapter stuck at Processing (RetryFor is 0 and the
        // UI only offers the action from Detected), wedging it until the DB was edited.
        var splitApplication = Substitute.For<ISplitApplicationService>();
        splitApplication
            .ApplySplitsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var stateManager = Substitute.For<ISplitProcessingStateManager>();

        using var provider = new ServiceCollection()
            .AddSingleton(splitApplication)
            .AddSingleton(stateManager)
            .BuildServiceProvider();

        var task = new ApplySplitsTask(7, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            task.ProcessAsync(provider, TestContext.Current.CancellationToken)
        );

        await stateManager.Received(1).SetFailedAsync(7, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessAsync_WhenApplyIsCancelled_DoesNotMarkTheStateFailed()
    {
        // Cancellation is not a failure; the requeue path owns the state.
        var splitApplication = Substitute.For<ISplitApplicationService>();
        splitApplication
            .ApplySplitsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));
        var stateManager = Substitute.For<ISplitProcessingStateManager>();

        using var provider = new ServiceCollection()
            .AddSingleton(splitApplication)
            .AddSingleton(stateManager)
            .BuildServiceProvider();

        var task = new ApplySplitsTask(7, 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            task.ProcessAsync(provider, TestContext.Current.CancellationToken)
        );

        await stateManager
            .DidNotReceiveWithAnyArgs()
            .SetFailedAsync(default, default, TestContext.Current.CancellationToken);
    }
}
