using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
/// Behavioural coverage for the local quiet-hours pause. Only the upscale pipeline pauses: a window
/// covering "now" must leave a queued upscale task unclaimed, and the processor must claim it once the
/// window ends. The windows are derived from the real local clock (the gate uses
/// <see cref="TimeProvider.System" /> in production), so the assertions are about the loop, not about
/// a substituted gate.
/// </summary>
public abstract class QuietHoursProcessorTestsBase : IDisposable
{
    protected readonly ITaskPersistenceService Persistence;
    protected readonly TaskQueue Queue;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    protected QuietHoursProcessorTestsBase()
    {
        var services = new ServiceCollection();
        // The name is computed once: a Guid inside the options callback would give every scope its own
        // database, and the processors check the row in their own scope.
        string databaseName = $"TestDb_QuietHours_{Guid.NewGuid()}";
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(databaseName)
        );

        Persistence = Substitute.For<ITaskPersistenceService>();
        Persistence.ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var cleanup = Substitute.For<IQueueCleanup>();
        cleanup.CleanupAsync().Returns(Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()));
        services.AddScoped<IQueueCleanup>(_ => cleanup);
        services.AddSingleton(Persistence);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        Queue = new TaskQueue(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TaskQueue>>()
        );
    }

    protected IServiceScopeFactory ScopeFactory =>
        _provider.GetRequiredService<IServiceScopeFactory>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Local wall-clock time of day, the clock the gate reads in production.</summary>
    protected static TimeSpan LocalNowOfDay() => TimeProvider.System.GetLocalNow().TimeOfDay;

    /// <summary>Wraps a time of day into [00:00, 24:00) so a window straddling midnight stays valid.</summary>
    protected static TimeSpan WrapAroundDay(TimeSpan value)
    {
        long ticksPerDay = TimeSpan.FromDays(1).Ticks;
        long ticks = value.Ticks % ticksPerDay;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + ticksPerDay : ticks);
    }

    /// <summary>
    /// A gate whose window covers "now": it opened <paramref name="back" /> ago and closes
    /// <paramref name="forward" /> from now.
    /// </summary>
    protected static IQuietHoursGate GateCoveringNowFor(TimeSpan back, TimeSpan forward) =>
        GateOpeningAfter(-back, back + forward);

    /// <summary>
    /// A gate whose window opens <paramref name="opensAfter" /> from now and lasts
    /// <paramref name="duration" />. Create it as late as possible: the times are computed when it is
    /// constructed.
    /// </summary>
    protected static IQuietHoursGate GateOpeningAfter(TimeSpan opensAfter, TimeSpan duration) =>
        new QuietHoursGate(
            Options.Create(
                new QuietHoursConfig
                {
                    Enabled = true,
                    Start = WrapAroundDay(LocalNowOfDay() + opensAfter),
                    End = WrapAroundDay(LocalNowOfDay() + opensAfter + duration),
                }
            ),
            TimeProvider.System
        );
}

public class UpscaleTaskProcessorQuietHoursTests : QuietHoursProcessorTestsBase
{
    private UpscaleTaskProcessor CreateProcessor(IQuietHoursGate gate, bool remoteOnly = false) =>
        new(
            Queue,
            ScopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = remoteOnly }),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            Persistence,
            new PreprocessedInputCache(),
            gate
        );

    /// <summary>
    /// Records which tasks the processor starts, in order. A rerouted task is never claimed here, so
    /// this is the only visible sign that it ran.
    /// </summary>
    private static (
        TaskCompletionSource<PersistedTask> First,
        Func<int[]> StartedIds
    ) ObserveStarts(UpscaleTaskProcessor processor)
    {
        var first = new TaskCompletionSource<PersistedTask>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var ids = new List<int>();
        processor.StatusChanged += task =>
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                lock (ids)
                {
                    ids.Add(task.Id);
                }

                first.TrySetResult(task);
            }

            return Task.CompletedTask;
        };
        return (
            first,
            () =>
            {
                lock (ids)
                {
                    return [.. ids];
                }
            }
        );
    }

    /// <summary>
    /// Persists a task row that is already claimed, without touching the queue: enqueueing would also
    /// signal the main channel, and this task is meant to arrive through the rerouted channel only.
    /// </summary>
    private async Task<PersistedTask> PersistClaimedUpscaleTaskAsync(CancellationToken ct)
    {
        using IServiceScope scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new PersistedTask
        {
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
            Status = PersistedTaskStatus.Processing,
        };
        db.PersistedTasks.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_DoesNotClaimWhileQuietHoursAreActive()
    {
        UpscaleTaskProcessor processor = CreateProcessor(
            GateCoveringNowFor(TimeSpan.FromHours(2), TimeSpan.FromHours(2))
        );
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await processor.StartAsync(cts.Token);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        await Persistence
            .DidNotReceive()
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Single(Queue.GetUpscaleSnapshot());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ClaimsATaskEnqueuedDuringTheWindowOnceItEnds()
    {
        // Enqueued while the window is open: the signal must survive the pause, and the task must be
        // claimed exactly once when it ends. A loop that consumed the signal before checking the
        // schedule would leave the task queued with nothing to wake it.
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                claimed.TrySetResult();
                return true;
            });

        UpscaleTaskProcessor processor = CreateProcessor(
            GateCoveringNowFor(TimeSpan.FromHours(1), TimeSpan.FromSeconds(2))
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await Task.Delay(400, TestContext.Current.CancellationToken);
        await Persistence
            .DidNotReceive()
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        await claimed.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken
        );

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_DoesNotClaimATaskEnqueuedAfterTheWindowOpenedWhileParked()
    {
        // Regression guard: the processor used to gate only at the top of the loop, so a host that
        // was idle when the window opened was parked in the channel wait and started the first
        // chapter enqueued afterwards. The check has to run after that wait resolves too.
        UpscaleTaskProcessor processor = CreateProcessor(
            GateOpeningAfter(TimeSpan.FromSeconds(2), TimeSpan.FromHours(1))
        );
        var (_, startedIds) = ObserveStarts(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        // Long enough for the loop to park in the channel wait and for the window to open.
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await Task.Delay(600, TestContext.Current.CancellationToken);

        await Persistence
            .DidNotReceive()
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Empty(startedIds());
        Assert.Single(Queue.GetUpscaleSnapshot());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ProcessesAReroutedTaskSentDuringTheWindowExactlyOnceAfterIt()
    {
        // A rerouted task that arrives while the loop is parked must wait for the window to close and
        // then run exactly once, as itself: neither dropped nor duplicated by the wait and dispatch.
        CancellationToken ct = TestContext.Current.CancellationToken;
        PersistedTask rerouted = await PersistClaimedUpscaleTaskAsync(ct);

        UpscaleTaskProcessor processor = CreateProcessor(
            GateOpeningAfter(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5))
        );
        var (first, startedIds) = ObserveStarts(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await Task.Delay(400, ct);

        // Still inside the window: nothing may have started.
        Assert.Empty(startedIds());

        PersistedTask started = await first.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(rerouted.Id, started.Id);

        // Give a duplicate a chance to appear before asserting the count.
        await Task.Delay(600, ct);
        Assert.Equal([rerouted.Id], startedIds());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoteOnly_DoesNotProcessAReroutedTaskThatArrivesDuringTheWindow()
    {
        // The discriminating interleaving: the loop is already parked in its channel wait when the
        // window opens, and the task arrives while it is open. Gating only before the wait would let
        // the task start immediately.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        PersistedTask rerouted = Queue.GetUpscaleSnapshot().Single();
        rerouted.Status = PersistedTaskStatus.Processing;

        // Opens 2 s after start and closes 1.5 s later, so arrival at 2.5 s is inside it.
        UpscaleTaskProcessor processor = CreateProcessor(
            GateOpeningAfter(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5)),
            remoteOnly: true
        );
        var (first, startedIds) = ObserveStarts(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);

        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await Task.Delay(400, ct);

        Assert.Empty(startedIds());

        PersistedTask started = await first.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(rerouted.Id, started.Id);

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }
}
