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
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_QuietHours_{Guid.NewGuid()}")
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
        new QuietHoursGate(
            Options.Create(
                new QuietHoursConfig
                {
                    Enabled = true,
                    Start = WrapAroundDay(LocalNowOfDay() - back),
                    End = WrapAroundDay(LocalNowOfDay() + forward),
                }
            ),
            TimeProvider.System
        );
}

public class UpscaleTaskProcessorQuietHoursTests : QuietHoursProcessorTestsBase
{
    private UpscaleTaskProcessor CreateProcessor(IQuietHoursGate gate) =>
        new(
            Queue,
            ScopeFactory,
            Options.Create(new UpscalerConfig { RemoteOnly = false }),
            Substitute.For<ILogger<UpscaleTaskProcessor>>(),
            Persistence,
            new PreprocessedInputCache(),
            gate
        );

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
    public async Task ExecuteAsync_ClaimsOnceTheWindowEnds()
    {
        await Queue.EnqueueAsync(new UpscaleTask { ChapterId = 1, UpscalerProfileId = 1 });
        int taskId = Queue.GetUpscaleSnapshot().Single().Id;

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Persistence
            .ClaimTaskAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if ((int)ci[0] == taskId)
                {
                    claimed.TrySetResult();
                }

                return true;
            });

        UpscaleTaskProcessor processor = CreateProcessor(
            GateCoveringNowFor(TimeSpan.FromHours(1), TimeSpan.FromSeconds(2))
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await processor.StartAsync(cts.Token);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        await Persistence.DidNotReceive().ClaimTaskAsync(taskId, Arg.Any<CancellationToken>());

        await claimed.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken
        );

        await cts.CancelAsync();
        await processor.StopAsync(CancellationToken.None);
    }
}
