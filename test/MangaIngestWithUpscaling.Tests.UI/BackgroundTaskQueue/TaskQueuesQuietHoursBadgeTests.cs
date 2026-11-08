using System;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.BackgroundTaskQueue;

/// <summary>
/// The tasks page carries a quiet-hours badge so an operator can see why nothing is being claimed.
/// The gate is read from DI, so the page cannot render at all unless it is registered — that
/// registration is part of what these tests pin.
/// </summary>
public class TaskQueuesQuietHoursBadgeTests : BunitContext
{
    private readonly IQuietHoursGate _gate = Substitute.For<IQuietHoursGate>();

    public TaskQueuesQuietHoursBadgeTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(Substitute.For<ILogger<TaskQueues>>());
        Services.AddSingleton(Substitute.For<ILogger<TaskTable>>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        Services.AddSingleton(_gate);

        var queue = Substitute.For<TaskQueue>(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskQueue>>()
        );
        Services.AddSingleton(queue);
        var standard = Substitute.For<StandardTaskProcessor>(
            queue,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        Services.AddSingleton(standard);
        var distributed = Substitute.For<DistributedUpscaleTaskProcessor>(
            queue,
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        Services.AddSingleton(distributed);
        Services.AddSingleton(
            Substitute.For<UpscaleTaskProcessor>(
                queue,
                Substitute.For<IServiceScopeFactory>(),
                Options.Create(new UpscalerConfig()),
                Substitute.For<ILogger<UpscaleTaskProcessor>>(),
                Substitute.For<ITaskPersistenceService>(),
                new PreprocessedInputCache(),
                _gate
            )
        );

        // A registry substitute returns empty snapshots, so both task tables render their empty state.
        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer.GetTitleAsync(Arg.Any<BaseTask>()).Returns(Task.FromResult("task"));
        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        Services.AddSingleton(factory);
        Services.AddSingleton(
            Substitute.For<TaskRegistry>(
                Substitute.For<IServiceScopeFactory>(),
                queue,
                standard,
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
    public void Badge_IsRenderedWhileQuietHoursAreActive()
    {
        _gate.IsQuiet.Returns(true);
        _gate.ResumeAtLocal.Returns(new TimeOnly(6, 0));

        var cut = Render<TaskQueues>();

        var badge = cut.Find("[data-testid='quiet-hours-badge']");
        Assert.Contains("paused until", badge.TextContent, StringComparison.Ordinal);
        Assert.Contains("06:00", badge.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Badge_IsAbsentWhenTheScheduleIsOpen()
    {
        _gate.IsQuiet.Returns(false);
        _gate.ResumeAtLocal.Returns((TimeOnly?)null);

        var cut = Render<TaskQueues>();

        Assert.Empty(cut.FindAll("[data-testid='quiet-hours-badge']"));
        // The page itself still renders, so an absent badge is a missing badge and not a failed page.
        Assert.NotEmpty(cut.FindAll("h4"));
    }
}
