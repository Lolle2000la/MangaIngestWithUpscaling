using MangaIngestWithUpscaling.Shared.Services.Inference;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// The executor every inference call is pinned to.
/// <para>
/// ONNX Runtime's CUDA provider allocates a per-thread context the first time a thread runs a session.
/// Measured on an RTX 5070 Ti: one thread that had run inference held 74 MB more than the process
/// before it, and a second thread added another 74 MB, while a run confined to one thread added
/// nothing per page. The page used to run on whichever pool thread <c>Task.Run</c> picked, so a
/// chapter paid that again for every thread it touched. These pin the properties the engine relies on.
/// </para>
/// </summary>
public class SingleThreadInferenceExecutorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ReusesOneThreadThatIsNotTheCallers()
    {
        int caller = Environment.CurrentManagedThreadId;
        int first = SingleThreadInferenceExecutor.Run(() => Environment.CurrentManagedThreadId);
        int second = SingleThreadInferenceExecutor.Run(() => Environment.CurrentManagedThreadId);

        Assert.Equal(first, second);
        Assert.NotEqual(caller, first);
        Assert.Equal(
            "onnx-inference",
            SingleThreadInferenceExecutor.Run(() => Thread.CurrentThread.Name)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CarriesTheCallersExecutionContext()
    {
        // The tiler's AsyncLocal test seam and the loggers' scopes live in the execution context. The
        // work runs on another thread, so without capture the page would lose them — and the engine's
        // own tests would see their seam disappear.
        var flow = new AsyncLocal<string?>();
        flow.Value = "caller-scope";
        try
        {
            Assert.Equal("caller-scope", SingleThreadInferenceExecutor.Run(() => flow.Value));
        }
        finally
        {
            flow.Value = null;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PropagatesTheFailureToTheCaller()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            SingleThreadInferenceExecutor.Run<object?>(() =>
                throw new InvalidOperationException("the run failed")
            )
        );

        Assert.Equal("the run failed", thrown.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunsNestedCallsInlineInsteadOfQueueingBehindItself()
    {
        // The worker blocks until its item is done, so a nested call that queued would wait for a
        // result only that same thread can produce.
        Task<int> nested = Task.Run(
            () =>
                SingleThreadInferenceExecutor.Run(() =>
                    SingleThreadInferenceExecutor.Run(() => Environment.CurrentManagedThreadId)
                ),
            TestContext.Current.CancellationToken
        );

        int inner = await nested.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            SingleThreadInferenceExecutor.Run(() => Environment.CurrentManagedThreadId),
            inner
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SerialisesConcurrentCallers()
    {
        int inside = 0;
        int mostInside = 0;
        var gate = new object();

        Parallel.For(
            0,
            8,
            _ =>
                SingleThreadInferenceExecutor.Run(() =>
                {
                    int now = Interlocked.Increment(ref inside);
                    lock (gate)
                    {
                        mostInside = Math.Max(mostInside, now);
                    }

                    Thread.Sleep(20);
                    Interlocked.Decrement(ref inside);
                    return 0;
                })
        );

        Assert.Equal(1, mostInside);
    }
}
