using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.BackgroundTaskQueue;

/// <summary>
/// The tasks page carries a quiet-hours badge so an operator can see why nothing is being claimed.
/// <para>
/// These tests drive the real <see cref="QuietHoursGate" /> and the page's real 30-second refresh
/// timer over a hand-written <see cref="TimeProvider" />, so the badge appearing and — the behaviour
/// that matters — disappearing when the schedule ends is exercised end to end rather than assumed.
/// The host's own DI registration is pinned separately by <c>SqliteApplicationStartupTests</c>; a
/// bUnit test cannot prove it because it registers its own services.
/// </para>
/// </summary>
public class TaskQueuesQuietHoursBadgeTests : BunitContext
{
    /// <summary>2026-01-05 23:00 UTC, inside the 22:00-06:00 window configured below.</summary>
    internal static readonly DateTimeOffset QuietInstant = new(2026, 1, 5, 23, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(QuietInstant);

    public TaskQueuesQuietHoursBadgeTests()
    {
        TaskQueuesPageTestHost.Register(
            this,
            _clock,
            new QuietHoursConfig
            {
                Enabled = true,
                Start = TimeSpan.FromHours(22),
                End = TimeSpan.FromHours(6),
            }
        );
    }

    [Fact]
    public void Badge_IsRenderedWhileQuietHoursAreActive()
    {
        var cut = Render<TaskQueues>();

        var badge = cut.Find("[data-testid='quiet-hours-badge']");
        Assert.Contains("paused until", badge.TextContent, StringComparison.Ordinal);
        // 23:00 inside 22:00-06:00 resumes at 06:00.
        Assert.Contains("06:00", badge.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Badge_IsAbsentWhenTheScheduleIsOpen()
    {
        // 12:00 UTC is the middle of the day.
        _clock.SetUtcNow(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));

        var cut = Render<TaskQueues>();

        Assert.Empty(cut.FindAll("[data-testid='quiet-hours-badge']"));
        // The page itself still renders, so an absent badge is a missing badge and not a failed page.
        Assert.NotEmpty(cut.FindAll("h4"));
    }

    [Fact]
    public void Badge_DisappearsWhenTheRefreshTimerObservesTheWindowEnding()
    {
        // The refresh is a 30 s poll, so without a tick the page would keep a stale badge forever.
        // Jumping the clock past 06:00 and letting the timer fire is exactly what happens on a live
        // circuit when the window ends.
        var cut = Render<TaskQueues>();
        Assert.NotEmpty(cut.FindAll("[data-testid='quiet-hours-badge']"));

        cut.InvokeAsync(() =>
        {
            _clock.Advance(TimeSpan.FromHours(7));
            return Task.CompletedTask;
        });

        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid='quiet-hours-badge']")),
            TimeSpan.FromSeconds(5)
        );
    }

    [Fact]
    public async Task Refresh_RendersOnAStateChangeAndStaysQuietOtherwise()
    {
        // Two claims in one: the boundary crossing must produce a render (otherwise a refresh loop
        // that was deleted would pass the second half), and a tick with no state change must not.
        var cut = Render<TaskQueues>();
        int rendersBeforeBoundary = cut.RenderCount;

        await cut.InvokeAsync(() =>
        {
            _clock.Advance(TimeSpan.FromHours(7));
            return Task.CompletedTask;
        });

        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid='quiet-hours-badge']")),
            TimeSpan.FromSeconds(5)
        );
        Assert.True(
            cut.RenderCount > rendersBeforeBoundary,
            "crossing the window end must re-render the page"
        );

        int rendersAfterBoundary = cut.RenderCount;
        await cut.InvokeAsync(() =>
        {
            _clock.Advance(TimeSpan.FromSeconds(90));
            return Task.CompletedTask;
        });
        await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(rendersAfterBoundary, cut.RenderCount);
    }
}

/// <summary>
/// A schedule that covers every hour has no resume time, so the badge must say so instead of naming a
/// boundary that never arrives.
/// </summary>
public class TaskQueuesQuietHoursBadgeNoEndTests : BunitContext
{
    private readonly ManualTimeProvider _clock = new(TaskQueuesQuietHoursBadgeTests.QuietInstant);

    public TaskQueuesQuietHoursBadgeNoEndTests()
    {
        TaskQueuesPageTestHost.Register(
            this,
            _clock,
            new QuietHoursConfig
            {
                Enabled = true,
                Windows =
                [
                    new QuietWindow { Start = TimeSpan.Zero, End = TimeSpan.FromHours(12) },
                    new QuietWindow { Start = TimeSpan.FromHours(12), End = TimeSpan.Zero },
                ],
            }
        );
    }

    [Fact]
    public void Badge_OmitsTheTimeWhenTheScheduleHasNoEnd()
    {
        var cut = Render<TaskQueues>();

        var badge = cut.Find("[data-testid='quiet-hours-badge']");
        Assert.Contains("Quiet hours — paused", badge.TextContent, StringComparison.Ordinal);
        // Nothing that looks like a resume time, because there is none.
        Assert.DoesNotContain("06:00", badge.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("paused until", badge.TextContent, StringComparison.Ordinal);
    }
}

/// <summary>
/// Registers the services the tasks page and its two task tables need, with the real gate over the
/// supplied clock so the page's badge follows a schedule that can be driven.
/// </summary>
internal static class TaskQueuesPageTestHost
{
    public static void Register(BunitContext context, TimeProvider clock, QuietHoursConfig config)
    {
        IServiceCollection services = context.Services;
        services.AddMudServices();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        services.AddSingleton(Substitute.For<ILogger<TaskQueues>>());
        services.AddSingleton(Substitute.For<ILogger<TaskTable>>());
        services.AddSingleton(Substitute.For<ISnackbar>());
        // The page's refresh timer and the gate read the same clock, so advancing it moves both.
        services.AddSingleton(clock);
        services.AddSingleton<IQuietHoursGate>(sp => new QuietHoursGate(
            Options.Create(config),
            sp.GetRequiredService<TimeProvider>()
        ));

        var queue = Substitute.For<TaskQueue>(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskQueue>>()
        );
        services.AddSingleton(queue);
        var standard = Substitute.For<StandardTaskProcessor>(
            queue,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<StandardTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        services.AddSingleton(standard);
        var distributed = Substitute.For<DistributedUpscaleTaskProcessor>(
            queue,
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<DistributedUpscaleTaskProcessor>>(),
            Substitute.For<ITaskPersistenceService>()
        );
        services.AddSingleton(distributed);
        services.AddSingleton(
            Substitute.For<UpscaleTaskProcessor>(
                queue,
                Substitute.For<IServiceScopeFactory>(),
                Options.Create(new UpscalerConfig()),
                Substitute.For<ILogger<UpscaleTaskProcessor>>(),
                Substitute.For<ITaskPersistenceService>(),
                new PreprocessedInputCache(),
                Substitute.For<IQuietHoursGate>()
            )
        );

        // A registry substitute returns empty snapshots, so both task tables render their empty state.
        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer.GetTitleAsync(Arg.Any<BaseTask>()).Returns(Task.FromResult("task"));
        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        services.AddSingleton(factory);
        services.AddSingleton(
            Substitute.For<TaskRegistry>(
                Substitute.For<IServiceScopeFactory>(),
                queue,
                standard,
                null!,
                distributed,
                null!
            )
        );

        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        context.JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        context.JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        context.JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        context.JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        context.JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        context.JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
    }
}

/// <summary>
/// A hand-written <see cref="TimeProvider" /> with an exact, settable UTC instant and timers that fire
/// when it is advanced. Each timer fires at most once per <see cref="Advance" />, which is all a
/// coarse poll needs and keeps a large jump from replaying hundreds of ticks.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now) => _now = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan delta)
    {
        _now += delta;
        foreach (ManualTimer timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    private void Remove(ManualTimer timer) => _timers.Remove(timer);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _due;
        private TimeSpan _period;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _due = DueAt(dueTime);
            _period = period;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_due is not DateTimeOffset dueAt || now < dueAt)
            {
                return;
            }

            // Reschedule before invoking, so a callback that changes the timer is not overwritten.
            _due = DueAt(_period);
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = DueAt(dueTime);
            _period = period;
            return true;
        }

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        private DateTimeOffset? DueAt(TimeSpan delay) =>
            delay == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + delay;
    }
}
