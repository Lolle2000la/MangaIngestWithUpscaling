using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.BackgroundTaskQueue;

/// <summary>
/// Covers the <c>/tasks</c> page toolbar and row actions. The two <see cref="TaskTable"/>s are
/// registry-driven, so a real <see cref="TaskRegistry"/> is populated through its internal change
/// pipeline; the page's queue and processors are substitutes so the actions can be verified.
/// </summary>
public class TaskQueuesTests : BunitContext
{
    private readonly ITaskQueue _queue = Substitute.For<ITaskQueue>();
    private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();
    private readonly StandardTaskProcessor _standardProcessor;
    private readonly UpscaleTaskProcessor _upscaleProcessor;
    private readonly DistributedUpscaleTaskProcessor _distributedProcessor;
    private readonly TaskRegistry _registry;

    public TaskQueuesTests()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var concreteQueue = Substitute.For<TaskQueue>(
            scopeFactory,
            Substitute.For<ILogger<TaskQueue>>()
        );
        _standardProcessor = Substitute.For<StandardTaskProcessor>(
            concreteQueue,
            scopeFactory,
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        _upscaleProcessor = Substitute.For<UpscaleTaskProcessor>(
            concreteQueue,
            scopeFactory,
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>(),
            Substitute.For<IPreprocessedInputCache>()
        );
        _distributedProcessor = Substitute.For<DistributedUpscaleTaskProcessor>(
            concreteQueue,
            scopeFactory,
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        _registry = new TaskRegistry(
            scopeFactory,
            concreteQueue,
            _standardProcessor,
            _upscaleProcessor,
            _distributedProcessor,
            NullLogger<TaskRegistry>.Instance
        );

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(Substitute.For<ILogger<TaskQueues>>());
        Services.AddSingleton(Substitute.For<ILogger<TaskTable>>());
        Services.AddSingleton(_queue);
        Services.AddSingleton(_standardProcessor);
        Services.AddSingleton(_upscaleProcessor);
        Services.AddSingleton(_distributedProcessor);
        Services.AddSingleton(_registry);
        Services.AddSingleton(_snackbar);

        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer
            .GetTitleAsync(Arg.Any<BaseTask>())
            .Returns(ci => Task.FromResult(TitleOf(ci.Arg<BaseTask>())));
        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        Services.AddSingleton(factory);

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
        JSInterop.SetupVoid("localTime.formatElement").SetVoidResult();
    }

    [Fact]
    public async Task ClearCompleted_Standard_RemovesOnlyCompletedStandardTasks()
    {
        await SeedRegistryAsync(
            StandardTask(1, PersistedTaskStatus.Completed),
            StandardTask(2, PersistedTaskStatus.Failed),
            StandardTask(3, PersistedTaskStatus.Pending)
        );
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnClearCompleted.InvokeAsync());

        await _queue
            .Received(1)
            .RemoveTasksAsync(
                Arg.Is<IEnumerable<PersistedTask>>(tasks => Ids(tasks).SequenceEqual(new[] { 1 }))
            );
    }

    [Fact]
    public async Task ClearFailed_Standard_RemovesOnlyFailedStandardTasks()
    {
        await SeedRegistryAsync(
            StandardTask(4, PersistedTaskStatus.Completed),
            StandardTask(5, PersistedTaskStatus.Failed),
            StandardTask(6, PersistedTaskStatus.Failed)
        );
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnClearFailed.InvokeAsync());

        await _queue
            .Received(1)
            .RemoveTasksAsync(
                Arg.Is<IEnumerable<PersistedTask>>(tasks =>
                    Ids(tasks).SequenceEqual(new[] { 5, 6 })
                )
            );
    }

    [Fact]
    public async Task ClearCompleted_Upscale_RemovesOnlyCompletedUpscaleTasks()
    {
        await SeedRegistryAsync(
            UpscaleTask(13, PersistedTaskStatus.Completed),
            UpscaleTask(14, PersistedTaskStatus.Failed),
            UpscaleTask(15, PersistedTaskStatus.Pending)
        );
        var cut = RenderTaskQueues();
        TaskTable upscaleTable = cut.FindComponents<TaskTable>()[1].Instance;

        await cut.InvokeAsync(() => upscaleTable.OnClearCompleted.InvokeAsync());

        await _queue
            .Received(1)
            .RemoveTasksAsync(
                Arg.Is<IEnumerable<PersistedTask>>(tasks => Ids(tasks).SequenceEqual(new[] { 13 }))
            );
    }

    [Fact]
    public async Task ClearFailed_Upscale_RemovesOnlyFailedUpscaleTasks()
    {
        await SeedRegistryAsync(
            UpscaleTask(11, PersistedTaskStatus.Completed),
            UpscaleTask(12, PersistedTaskStatus.Failed)
        );
        var cut = RenderTaskQueues();
        TaskTable upscaleTable = cut.FindComponents<TaskTable>()[1].Instance;

        await cut.InvokeAsync(() => upscaleTable.OnClearFailed.InvokeAsync());

        await _queue
            .Received(1)
            .RemoveTasksAsync(
                Arg.Is<IEnumerable<PersistedTask>>(tasks => Ids(tasks).SequenceEqual(new[] { 12 }))
            );
    }

    [Fact]
    public async Task RetryFailed_InvokesQueueRetryWithTheTask()
    {
        var task = StandardTask(21, PersistedTaskStatus.Failed);
        await SeedRegistryAsync(task);
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnRetryFailed.InvokeAsync(task));

        await _queue.Received(1).RetryAsync(task);
    }

    [Fact]
    public async Task Delete_InvokesQueueRemoveWithTheTask()
    {
        var task = StandardTask(31, PersistedTaskStatus.Completed);
        await SeedRegistryAsync(task);
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnDelete.InvokeAsync(task));

        await _queue.Received(1).RemoveTaskAsync(task);
    }

    [Fact]
    public async Task RunNow_InvokesQueueMoveToFrontWithTheTask()
    {
        var task = StandardTask(41, PersistedTaskStatus.Pending);
        await SeedRegistryAsync(task);
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnRunNow.InvokeAsync(task));

        await _queue.Received(1).MoveToFrontAsync(task, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelStandard_InvokesStandardProcessorCancel()
    {
        var task = StandardTask(51, PersistedTaskStatus.Processing);
        await SeedRegistryAsync(task);
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnCancel.InvokeAsync(task));

        _standardProcessor.Received(1).CancelCurrent(task);
    }

    [Fact]
    public async Task CancelUpscale_InvokesBothUpscaleProcessorsCancel()
    {
        var task = UpscaleTask(61, PersistedTaskStatus.Processing);
        await SeedRegistryAsync(task);
        var cut = RenderTaskQueues();
        TaskTable upscaleTable = cut.FindComponents<TaskTable>()[1].Instance;

        await cut.InvokeAsync(() => upscaleTable.OnCancel.InvokeAsync(task));

        _upscaleProcessor.Received(1).CancelCurrent(task);
        await _distributedProcessor.Received(1).CancelCurrent(task);
    }

    [Fact]
    public async Task ClearCompleted_WhenQueueThrows_ShowsErrorSnackbarAndLeavesComponentsRendered()
    {
        await SeedRegistryAsync(StandardTask(71, PersistedTaskStatus.Completed));
        _queue
            .RemoveTasksAsync(Arg.Any<IEnumerable<PersistedTask>>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var cut = RenderTaskQueues();
        TaskTable standardTable = cut.FindComponents<TaskTable>()[0].Instance;

        await cut.InvokeAsync(() => standardTable.OnClearCompleted.InvokeAsync());

        _snackbar
            .Received(1)
            .Add(Arg.Is<string>(message => message.Contains("boom")), Severity.Error);
        // The handler swallowed the failure, so both tables must still be alive and rendered.
        Assert.Equal(2, cut.FindComponents<TaskTable>().Count);
        Assert.Contains("task-71", cut.Markup);
    }

    private IRenderedComponent<TaskQueues> RenderTaskQueues() => Render<TaskQueues>();

    private async Task SeedRegistryAsync(params PersistedTask[] tasks)
    {
        foreach (PersistedTask task in tasks)
        {
            await _registry.OnTaskChanged(task);
        }

        _registry.FlushPendingUpdates();
    }

    private static PersistedTask StandardTask(int id, PersistedTaskStatus status) =>
        new()
        {
            Id = id,
            Data = new LoggingTask { Message = $"task-{id}" },
            Status = status,
            Order = id,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        };

    private static PersistedTask UpscaleTask(int id, PersistedTaskStatus status) =>
        new()
        {
            Id = id,
            Data = new DetectSplitCandidatesTask(1, 1),
            Status = status,
            Order = id,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        };

    private static IEnumerable<int> Ids(IEnumerable<PersistedTask> tasks) =>
        tasks.Select(task => task.Id).OrderBy(id => id);

    private static string TitleOf(BaseTask task) =>
        task switch
        {
            LoggingTask logging => logging.Message,
            _ => task.GetType().Name,
        };
}
