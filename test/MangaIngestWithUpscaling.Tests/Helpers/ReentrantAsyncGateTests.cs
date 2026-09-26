using MangaIngestWithUpscaling.Helpers;

namespace MangaIngestWithUpscaling.Tests.Helpers;

public class ReentrantAsyncGateTests
{
    [Fact]
    public async Task RunAsync_SerializesConcurrentOperations()
    {
        using var gate = new ReentrantAsyncGate();
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
    public async Task RunAsync_AllowsReentrantCallsWithoutDeadlock()
    {
        using var gate = new ReentrantAsyncGate();
        int runs = 0;

        await gate.RunAsync(async () =>
        {
            runs++;
            await gate.RunAsync(async () =>
            {
                runs++;
                await Task.Yield();
            });
        });

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task RunAsync_ReleasesGateAfterFailure()
    {
        using var gate = new ReentrantAsyncGate();

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
