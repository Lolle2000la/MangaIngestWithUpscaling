using MangaIngestWithUpscaling.Helpers;

namespace MangaIngestWithUpscaling.Tests.Helpers;

public class AsyncGateTests
{
    [Fact]
    public async Task RunAsync_SerializesConcurrentOperations()
    {
        var gate = new AsyncGate();
        int active = 0;
        int maxActive = 0;

        async Task Work()
        {
            await gate.RunAsync(async () =>
            {
                int now = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref active);
            });
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Work()));

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task RunAsync_WorkSpawnedInsideTheHolderDoesNotBypassTheGate()
    {
        var gate = new AsyncGate();
        int active = 0;
        int maxActive = 0;
        var childRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await gate.RunAsync(async () =>
        {
            int now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);

            // A fire-and-forget task started by the holder must wait for the gate. A re-entrant gate
            // keyed on AsyncLocal would let this inherit the "already holds the gate" marker and run
            // alongside the holder, which is exactly the overlap the gate exists to prevent.
            _ = Task.Run(async () =>
            {
                await gate.RunAsync(() =>
                {
                    int child = Interlocked.Increment(ref active);
                    maxActive = Math.Max(maxActive, child);
                    Interlocked.Decrement(ref active);
                    return Task.CompletedTask;
                });
                childRan.TrySetResult();
            });

            await Task.Delay(100);
            Interlocked.Decrement(ref active);
        });

        // Prove the spawned work actually ran (and therefore acquired the gate) rather than being
        // dropped: maxActive == 1 alone would also hold if the child never executed.
        await childRan.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task RunAsync_ReleasesGateAfterFailure()
    {
        var gate = new AsyncGate();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(() => throw new InvalidOperationException("boom"))
        );

        // The gate must be free again for the next operation.
        bool ran = false;
        await gate.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }
}
