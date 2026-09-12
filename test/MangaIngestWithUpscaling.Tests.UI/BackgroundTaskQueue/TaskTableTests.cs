using System.Collections.Generic;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.BackgroundTaskQueue;

public class TaskTableTests : BunitContext
{
    public TaskTableTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(Substitute.For<ILogger<TaskTable>>());

        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer
            .GetTitleAsync(Arg.Any<BaseTask>())
            .Returns(ci => Task.FromResult(((LoggingTask)ci[0]).Message));
        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        Services.AddSingleton(factory);

        // A registry substitute returns empty snapshots, so any rendered row must have come from
        // the caller-supplied Tasks collection.
        var queue = Substitute.For<TaskQueue>(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskQueue>>()
        );
        var distributed = Substitute.For<DistributedUpscaleTaskProcessor>(
            queue,
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        Services.AddSingleton(distributed);
        Services.AddSingleton(
            Substitute.For<TaskRegistry>(
                Substitute.For<IServiceScopeFactory>(),
                queue,
                null!,
                null!,
                distributed,
                null!
            )
        );

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
    }

    [Fact]
    public void TasksParameter_WhenSupplied_PagesFromItAndIgnoresTheRegistry()
    {
        // 25 tasks with the table's 10-rows-per-page default: only the first page must render.
        List<PersistedTask> tasks = new();
        for (int i = 0; i < 25; i++)
        {
            tasks.Add(
                new PersistedTask
                {
                    Id = i + 1,
                    Data = new LoggingTask { Message = $"Task-{i}" },
                    Status = PersistedTaskStatus.Pending,
                    Order = i,
                }
            );
        }

        var cut = Render<TaskTable>(parameters =>
        {
            parameters.Add(p => p.Tasks, tasks);
            parameters.Add(p => p.Upscale, false);
        });

        cut.WaitForAssertion(() => Assert.Contains("Task-0", cut.Markup));
        Assert.Contains("Task-9", cut.Markup);
        Assert.DoesNotContain("Task-10", cut.Markup);
    }

    [Fact]
    public void TasksParameter_WhenNotSupplied_UsesTheRegistrySnapshot()
    {
        // Without Tasks the component falls back to the registry; the substitute returns an empty
        // snapshot, so the table renders its empty state without throwing.
        var cut = Render<TaskTable>(parameters => parameters.Add(p => p.Upscale, false));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.Markup));
        Assert.DoesNotContain("Task-0", cut.Markup);
    }
}
