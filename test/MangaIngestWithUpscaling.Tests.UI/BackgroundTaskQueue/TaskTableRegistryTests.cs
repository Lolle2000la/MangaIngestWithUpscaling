using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
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
/// Exercises <see cref="TaskTable"/> against a real <see cref="TaskRegistry"/> so live status
/// updates and data shrinkage drive the actual reload path rather than a stub snapshot.
/// </summary>
public class TaskTableRegistryTests : BunitContext
{
    private readonly TaskRegistry _registry;
    private readonly DistributedUpscaleTaskProcessor _distributedProcessor;

    public TaskTableRegistryTests()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var concreteQueue = Substitute.For<TaskQueue>(
            scopeFactory,
            Substitute.For<ILogger<TaskQueue>>()
        );
        var standardProcessor = Substitute.For<StandardTaskProcessor>(
            concreteQueue,
            scopeFactory,
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        var upscaleProcessor = Substitute.For<UpscaleTaskProcessor>(
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
            standardProcessor,
            upscaleProcessor,
            _distributedProcessor,
            NullLogger<TaskRegistry>.Instance
        );

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(Substitute.For<ILogger<TaskTable>>());
        Services.AddSingleton(_registry);
        Services.AddSingleton(_distributedProcessor);

        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer
            .GetTitleAsync(Arg.Any<BaseTask>())
            .Returns(ci => Task.FromResult(((LoggingTask)ci.Arg<BaseTask>()).Message));
        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        Services.AddSingleton(factory);

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("localTime.formatElement").SetVoidResult();
    }

    [Fact]
    public async Task LiveUpdate_PendingToCompleted_UpdatesTheRenderedStatus()
    {
        var task = NewTask(1, PersistedTaskStatus.Pending);
        await ApplyUpdateAsync(task);

        var cut = Render<TaskTable>(parameters => parameters.Add(p => p.Upscale, false));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("live-1", cut.Markup);
            Assert.Contains("Pending", cut.Markup);
        });

        task.Status = PersistedTaskStatus.Completed;
        task.ProcessedAt = DateTime.UtcNow;
        await ApplyUpdateAsync(task);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("live-1", cut.Markup);
            Assert.Contains("Completed", cut.Markup);
        });
    }

    [Fact]
    public async Task Shrink_ToExactlyThePageEnd_ResetsToFirstPageInsteadOfRenderingBlank()
    {
        for (int id = 1; id <= 25; id++)
        {
            await _registry.OnTaskChanged(NewTask(id, PersistedTaskStatus.Pending));
        }

        _registry.FlushPendingUpdates();

        var cut = Render<TaskTable>(parameters => parameters.Add(p => p.Upscale, false));
        var table = cut.FindComponent<MudTable<PersistedTask>>();
        cut.WaitForAssertion(() => Assert.Contains("live-1", cut.Markup));

        await cut.InvokeAsync(() => table.Instance.NavigateTo(2));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, table.Instance.CurrentPage);
            Assert.Contains("live-21", cut.Markup);
        });

        // Removing the five tasks on page 2 shrinks the set to exactly 20. MudTable only clamps
        // when the page start is strictly past the total, so without the component-side reset the
        // table would ask for page 2 (rows 20..29) of 20 rows and render empty.
        for (int id = 21; id <= 25; id++)
        {
            await _registry.OnTaskRemoved(NewTask(id, PersistedTaskStatus.Pending));
        }

        _registry.FlushPendingUpdates();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(0, table.Instance.CurrentPage);
            Assert.Contains("live-1", cut.Markup);
        });
    }

    private async Task ApplyUpdateAsync(PersistedTask task)
    {
        await _registry.OnTaskChanged(task);
        _registry.FlushPendingUpdates();
    }

    private static PersistedTask NewTask(int id, PersistedTaskStatus status) =>
        new()
        {
            Id = id,
            Data = new LoggingTask { Message = $"live-{id}" },
            Status = status,
            Order = id,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        };
}
