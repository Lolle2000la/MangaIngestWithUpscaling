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
    /// Records when the processor starts work, and how many times. A rerouted task is never claimed
    /// here, so this is the only visible sign that it ran.
    /// </summary>
    private static (TaskCompletionSource Started, Func<int> Count) ObserveStart(
        UpscaleTaskProcessor processor
    )
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        processor.StatusChanged += task =>
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                Interlocked.Increment(ref count);
                started.TrySetResult();
            }

            return Task.CompletedTask;
        };
        return (started, () => Volatile.Read(ref count));
    }

    /// <summary>A rerouted task: already claimed, so only its processed state is observable here.</summary>
    private static PersistedTask ClaimedUpscaleTask(int id) =>
        new()
        {
            Id = id,
            Data = new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 },
            Status = PersistedTaskStatus.Processing,
        };

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
        var (_, startedCount) = ObserveStart(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        // Long enough for the loop to park in the channel wait and for the window to open.
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await Task.Delay(600, TestContext.Current.CancellationToken);

        await Persistence
            .DidNotReceive()
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, startedCount());
        Assert.Single(Queue.GetUpscaleSnapshot());

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_HandsBackAReroutedTaskConsumedAsTheWindowOpens()
    {
        // A rerouted task that arrives after the loop parked in the wait is already consumed when the
        // window turns out to be open. Parking with it held would lose it on shutdown, so it has to go
        // back to the rerouted channel — and it must not be processed now.
        PersistedTask rerouted = ClaimedUpscaleTask(9001);
        UpscaleTaskProcessor processor = CreateProcessor(
            GateOpeningAfter(TimeSpan.FromSeconds(2), TimeSpan.FromHours(1))
        );
        var (_, startedCount) = ObserveStart(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await Task.Delay(800, TestContext.Current.CancellationToken);

        Assert.Equal(0, startedCount());
        Assert.True(
            await Queue.ReroutedUpscaleReader.WaitToReadAsync(cts.Token),
            "the consumed rerouted task must be back in its channel"
        );

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoteOnly_DoesNotProcessAReroutedTaskThatArrivesDuringTheWindow()
    {
        // The RemoteOnly loop used to gate before ReadAsync and then park in it, so a task rerouted
        // while the window was open was processed straight away. It must wait for the end instead,
        // and still be processed then.
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        PersistedTask rerouted = Queue.GetUpscaleSnapshot().Single();
        rerouted.Status = PersistedTaskStatus.Processing;

        UpscaleTaskProcessor processor = CreateProcessor(
            GateCoveringNowFor(TimeSpan.FromHours(1), TimeSpan.FromSeconds(2.5)),
            remoteOnly: true
        );
        var (started, startedCount) = ObserveStart(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.Equal(0, startedCount());

        await started.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken
        );

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }
}
