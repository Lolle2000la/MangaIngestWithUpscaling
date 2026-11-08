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
    private UpscaleTaskProcessor CreateProcessor(
        IQuietHoursGate gate,
        bool remoteOnly = false,
        ILogger<UpscaleTaskProcessor>? logger = null
    ) =>
        new(
            Queue,
            ScopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = remoteOnly }),
            logger ?? Substitute.For<ILogger<UpscaleTaskProcessor>>(),
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
        // chapter enqueued afterwards. The gate is scripted rather than clocked so the test knows
        // where the loop is instead of sleeping and hoping:
        //   * its first park returns immediately — proof the loop got past the gate and is now in the
        //     channel wait;
        //   * the second park is the post-wait check, and it blocks until the test releases it.
        var gate = new PassOnceGate();
        UpscaleTaskProcessor processor = CreateProcessor(gate);
        var (_, startedIds) = ObserveStarts(processor);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                claimed.TrySetResult();
                return true;
            });

        CancellationToken ct = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        await gate.PassedFirstGate.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        // The window is open from here on: this arrival must wait for it.
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await gate.QuietParked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Empty(startedIds());
        await Persistence
            .DidNotReceive()
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Single(Queue.GetUpscaleSnapshot());

        gate.Release();
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ProcessesAReroutedTaskSentDuringTheWindowExactlyOnceAfterIt()
    {
        // A rerouted task that arrives while the loop is parked must wait for the window to close and
        // then run exactly once, as itself: neither dropped nor duplicated by the wait and dispatch.
        // The gate reports one open read (proof the loop left the top-of-loop gate and is in its
        // channel wait) and then stays quiet until the test releases it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        PersistedTask rerouted = await PersistClaimedUpscaleTaskAsync(ct);

        var gate = new WindowOpensAfterFirstReadGate();
        UpscaleTaskProcessor processor = CreateProcessor(gate);
        var (first, startedIds) = ObserveStarts(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await gate.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        gate.OpenWindow();
        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await gate.QuietParked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        // Still inside the window: nothing may have started.
        Assert.Empty(startedIds());

        gate.Release();
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
        // The same script for the RemoteOnly loop, with one difference: that loop has no
        // top-of-loop gate to observe, so the window is opened before the task is sent and the
        // assertion that matters is that the arrival blocks in the gate instead of starting. It is
        // deterministic for the fixed loop; it cannot also be made discriminating against the old
        // loop without a wall-clock window, because the fixed loop offers no observable step between
        // start and its channel wait to place the window opening inside.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        PersistedTask rerouted = Queue.GetUpscaleSnapshot().Single();
        rerouted.Status = PersistedTaskStatus.Processing;

        var gate = new WindowOpensAfterFirstReadGate();
        UpscaleTaskProcessor processor = CreateProcessor(gate, remoteOnly: true);
        var (first, startedIds) = ObserveStarts(processor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        // The RemoteOnly loop has no top-of-loop gate — it parks in the channel wait straight away —
        // so there is nothing to observe before the arrival. Opening the window here is what makes
        // the arrival itself the thing that must wait, and the wait it must block in is asserted
        // below, so no sleep decides anything.
        gate.OpenWindow();
        await Queue.SendToLocalUpscaleAsync(rerouted, cts.Token);
        await gate.QuietParked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Empty(startedIds());

        gate.Release();
        PersistedTask started = await first.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(rerouted.Id, started.Id);

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_LogsTheResumptionAsTheWindowEndsNotAfterTheNextTask()
    {
        // The "resumed" line used to be emitted by the next iteration's poll, i.e. only after the
        // first post-window task finished (and never if that task outlived the process). It belongs
        // to the moment the park returns, before anything is claimed.
        var logger = new RecordingLogger<UpscaleTaskProcessor>();
        var gate = new PassOnceGate();
        UpscaleTaskProcessor processor = CreateProcessor(gate, logger: logger);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool resumedBeforeClaim = false;
        Persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                resumedBeforeClaim = logger.Count("Task claiming resumed") == 1;
                claimed.TrySetResult();
                return true;
            });

        CancellationToken ct = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);
        await gate.PassedFirstGate.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        await gate.QuietParked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        gate.Release();
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.True(resumedBeforeClaim, "the resumption must be logged as the window ends");
        // Exactly one line per transition in each direction.
        Assert.Equal(1, logger.Count("Task claiming paused by quiet hours until "));
        Assert.Equal(1, logger.Count("Task claiming resumed"));

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }

    /// <summary>Records the formatted messages, so log timing and count can be asserted.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public int Count(string prefix)
        {
            lock (_messages)
            {
                return _messages.Count(message =>
                    message.StartsWith(prefix, StringComparison.Ordinal)
                );
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>The state a scripted gate reports while the schedule is open.</summary>
    private static QuietHoursState QuietState =>
        new(true, TimeSpan.FromMinutes(5), new TimeOnly(6, 0));

    /// <summary>
    /// Scripts a gate whose first park returns immediately — evidence that the loop got past the
    /// top-of-loop gate — and whose later parks block until <see cref="Release" />. The schedule
    /// stays quiet throughout, so a loop that checks only once cannot claim anything.
    /// </summary>
    private sealed class PassOnceGate : IQuietHoursGate
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private volatile bool _quiet = true;
        private int _parks;

        public TaskCompletionSource PassedFirstGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource QuietParked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QuietHoursState Current => _quiet ? QuietState : default;

        public Task WaitUntilNotQuietAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _parks) == 1)
            {
                PassedFirstGate.TrySetResult();
                return Task.CompletedTask;
            }

            QuietParked.TrySetResult();
            return _release.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Ends the window: later reads report it open.</summary>
        public void Release()
        {
            _quiet = false;
            _release.TrySetResult();
        }
    }

    /// <summary>
    /// Scripts a gate that is open for its first read — so the loop passes the top-of-loop gate and
    /// parks in its channel wait — and quiet from the moment <see cref="OpenWindow" /> is called.
    /// The first quiet park blocks until <see cref="Release" />.
    /// </summary>
    private sealed class WindowOpensAfterFirstReadGate : IQuietHoursGate
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private volatile bool _quiet;
        private int _reads;

        public TaskCompletionSource FirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource QuietParked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QuietHoursState Current
        {
            get
            {
                bool quiet = _quiet;
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    FirstRead.TrySetResult();
                }

                return quiet ? QuietState : default;
            }
        }

        public Task WaitUntilNotQuietAsync(CancellationToken cancellationToken)
        {
            QuietParked.TrySetResult();
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void OpenWindow() => _quiet = true;

        /// <summary>Ends the window: later reads report it open.</summary>
        public void Release()
        {
            _quiet = false;
            _release.TrySetResult();
        }
    }
}
