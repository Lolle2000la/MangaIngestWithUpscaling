using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
///     Direct coverage for the cancellation surface extracted into
///     <see cref="BackgroundTaskProcessorBase" />. The task-processing paths remain covered through
///     the concrete processors' existing tests.
/// </summary>
public class BackgroundTaskProcessorBaseTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void CancelCurrent_CancelsOnlyWhenTheIdMatchesTheCurrentTask()
    {
        var processor = CreateProcessor();
        var current = new PersistedTask { Id = 1 };
        var other = new PersistedTask { Id = 2 };

        var currentToken = processor.BeginCurrentTaskForTest(current, CancellationToken.None);

        processor.CancelCurrent(other);
        Assert.False(currentToken.IsCancellationRequested);

        processor.CancelCurrent(current);
        Assert.True(currentToken.IsCancellationRequested);

        processor.DisposeCurrentTokenForTest(currentToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DisposeCurrentStoppingToken_IgnoresATokenThatIsNotCurrent()
    {
        var processor = CreateProcessor();
        var current = new PersistedTask { Id = 1 };

        var currentToken = processor.BeginCurrentTaskForTest(current, CancellationToken.None);
        using var unrelatedToken = new CancellationTokenSource();

        // Disposing a token that is not the current one must leave the real current task in place.
        processor.DisposeCurrentTokenForTest(unrelatedToken);
        processor.CancelCurrent(current);
        Assert.True(currentToken.IsCancellationRequested);

        processor.DisposeCurrentTokenForTest(currentToken);
    }

    private static ExposedBaseProcessor CreateProcessor() =>
        new(
            new TaskQueue(
                Substitute.For<IServiceScopeFactory>(),
                Substitute.For<ILogger<TaskQueue>>()
            ),
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger>(),
            Substitute.For<ITaskPersistenceService>()
        );

    private sealed class ExposedBaseProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        ITaskPersistenceService taskPersistenceService
    ) : BackgroundTaskProcessorBase(taskQueue, scopeFactory, logger, taskPersistenceService)
    {
        protected override string ProcessingFailedLogMessage => "test failure {TaskId}";

        protected override Task RunAsync(CancellationToken stoppingToken) => Task.CompletedTask;

        protected override Task<bool> TryAcquireTaskAsync(
            PersistedTask task,
            CancellationToken stoppingToken
        ) => Task.FromResult(false);

        public CancellationTokenSource BeginCurrentTaskForTest(
            PersistedTask task,
            CancellationToken stoppingToken
        ) => BeginCurrentTask(task, stoppingToken);

        public void DisposeCurrentTokenForTest(CancellationTokenSource token) =>
            DisposeCurrentStoppingToken(token);
    }
}
