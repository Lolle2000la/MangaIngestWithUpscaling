using System.Collections.Concurrent;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

/// <summary>
/// Concurrency coverage for <see cref="PageStreamSpool"/>. <see cref="PageStreamSpoolTests"/> pins the
/// semantics by interleaving calls sequentially; here the same operations are driven from dedicated
/// threads, because the spool's invariants (a bounded budget, atomic per-page writes, one identity per
/// chapter, one-shot assembly, no session lost to a concurrent finalize) are only meaningful when
/// operations actually overlap.
/// </summary>
[Collection("PageStreamSpoolSharedRoot")]
public class PageStreamSpoolConcurrencyTests
{
    private const string Identity = "identity";
    private const string Engine = "engine";
    private const string IdentityA = "identity-a";
    private const string IdentityB = "identity-b";

    /// <summary>
    /// Retention for the sweep tests. Deliberately long: <see cref="PageStreamSpool.SweepStale"/> also
    /// reclaims spool roots belonging to other <see cref="PageStreamSpool"/> instances, so a short
    /// window would delete the live roots of test classes running in parallel.
    /// </summary>
    private static readonly TimeSpan SweepRetention = TimeSpan.FromHours(1);

    private static readonly int Workers = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);

    private readonly PageStreamSpool _spool = new(
        Substitute.For<ILogger<PageStreamSpool>>(),
        Options.Create(new UpscalerConfig())
    );

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentManifestsWithOneIdentity_ShareOneLiveSession()
    {
        const int taskId = 200;
        var observed = new ConcurrentBag<PageStreamSession>();

        RunConcurrently(
            Workers,
            _ =>
            {
                for (int i = 0; i < 50; i++)
                {
                    observed.Add(_spool.GetOrCreateSession(taskId, Identity, Engine, 4));
                }
            }
        );

        // An unchanged identity must reuse the session rather than spawn competing spools for one task.
        PageStreamSession session = observed.First();
        Assert.All(observed, other => Assert.Same(session, other));
        Assert.Same(session, _spool.TryGetSession(taskId));
        Assert.False(_spool.IsFinalized(session));

        // Present but unusable would still break the caller: the session must accept a page.
        using (FileStream page = _spool.BeginPageWrite(session, 0, out _))
        {
            page.WriteByte(1);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentDistinctPageWrites_CommitEveryPageAndLeaveNoTempFiles()
    {
        const int taskId = 201;
        const int pageCount = 64;
        PageStreamSession session = _spool.GetOrCreateSession(taskId, Identity, Engine, pageCount);
        byte[][] payloads = Enumerable
            .Range(0, pageCount)
            .Select(i => Enumerable.Repeat((byte)(i % 251 + 1), 4 + (i % 7)).ToArray())
            .ToArray();
        var results = new ConcurrentQueue<CommitPageResult>();

        RunConcurrently(
            Workers,
            worker =>
            {
                for (int index = worker; index < pageCount; index += Workers)
                {
                    results.Enqueue(WritePage(session, Identity, Engine, index, payloads[index]));
                }
            }
        );

        Assert.All(results, result => Assert.Equal(CommitPageResult.Committed, result));
        Assert.True(_spool.IsComplete(session));
        Assert.Equal(
            Enumerable.Range(0, pageCount).ToArray(),
            _spool.GetCompletedPages(session).OrderBy(i => i).ToArray()
        );
        for (int index = 0; index < pageCount; index++)
        {
            Assert.Equal(payloads[index], File.ReadAllBytes(session.PagePath(index)));
        }

        Assert.Equal(payloads.Sum(p => (long)p.Length), _spool.GetTotalBytes(session));
        // Every temp file was renamed into place: the spool directory holds pages and nothing else.
        Assert.Equal(
            Enumerable.Range(0, pageCount).Select(i => PageFileName(i)).Order().ToArray(),
            Directory.GetFiles(session.Directory).Select(Path.GetFileName).Order().ToArray()
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentDuplicateUploads_NeverTearThePageFile()
    {
        const int taskId = 202;
        // Same length, different content: an interleaved write would show up as bytes from two writers.
        const int writers = 8;
        const int payloadLength = 4096;
        byte[][] payloads = Enumerable
            .Range(0, writers)
            .Select(w =>
                Enumerable.Range(0, payloadLength).Select(k => (byte)(w * 37 + k % 251)).ToArray()
            )
            .ToArray();
        PageStreamSession session = _spool.GetOrCreateSession(taskId, Identity, Engine, 1);
        var results = new ConcurrentQueue<CommitPageResult>();

        RunConcurrently(
            writers,
            worker =>
            {
                for (int i = 0; i < 25; i++)
                {
                    results.Enqueue(WritePage(session, Identity, Engine, 0, payloads[worker]));
                }
            }
        );

        Assert.All(results, result => Assert.Equal(CommitPageResult.Committed, result));
        Assert.Equal(new[] { 0 }, _spool.GetCompletedPages(session).ToArray());

        // Re-uploads replace each other; whichever won, the page is exactly one writer's payload.
        byte[] committed = File.ReadAllBytes(session.PagePath(0));
        Assert.Contains(payloads, payload => payload.SequenceEqual(committed));
        // The replaced bytes are not double-counted against the budget.
        Assert.Equal(committed.Length, _spool.GetTotalBytes(session));
        Assert.Equal(
            new[] { PageFileName(0) },
            Directory.GetFiles(session.Directory).Select(Path.GetFileName).ToArray()
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentCommits_RespectTheSpoolBudget()
    {
        const int taskId = 203;
        const int pages = 8;
        // Declared sizes rather than real bytes: the 8 GiB budget cannot be filled with actual files.
        long declaredSize = PageStreamSpool.MaxTaskBytes / 3;
        int budgetSlots = (int)(PageStreamSpool.MaxTaskBytes / declaredSize);
        PageStreamSession session = _spool.GetOrCreateSession(taskId, Identity, Engine, pages);
        var results = new ConcurrentQueue<CommitPageResult>();

        RunConcurrently(
            pages,
            worker =>
            {
                FileStream output = _spool.BeginPageWrite(session, worker, out string temp);
                using (output)
                {
                    output.WriteByte(1);
                }

                CommitPageResult result = _spool.TryCommitPage(
                    session,
                    Identity,
                    Engine,
                    worker,
                    temp,
                    declaredSize
                );
                if (result != CommitPageResult.Committed)
                {
                    TryDeleteTemp(temp);
                }

                results.Enqueue(result);
            }
        );

        // The budget is enforced under the gate, so the concurrent uploads cannot jointly exceed it:
        // exactly as many pages as fit are accepted and the rest are refused, in any completion order.
        Assert.Equal(budgetSlots, results.Count(result => result == CommitPageResult.Committed));
        Assert.Equal(
            pages - budgetSlots,
            results.Count(result => result == CommitPageResult.OverBudget)
        );
        Assert.Equal(declaredSize * budgetSlots, _spool.GetTotalBytes(session));
        Assert.True(_spool.GetTotalBytes(session) <= PageStreamSpool.MaxTaskBytes);
        Assert.Equal(
            ReadUnderGate(session, s => s.PageSizes.Values.Sum()),
            _spool.GetTotalBytes(session)
        );
        Assert.All(
            _spool.GetCompletedPages(session),
            index => Assert.True(File.Exists(session.PagePath(index)))
        );
        Assert.False(_spool.IsComplete(session));
        Assert.Empty(Directory.GetFiles(session.Directory, "*.tmp"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentReservations_RespectTheBudgetAndLeaveNothingInFlight()
    {
        const int taskId = 204;
        const int pages = 16;
        PageStreamSession session = _spool.GetOrCreateSession(taskId, Identity, Engine, pages);

        RunConcurrently(
            Workers,
            worker =>
            {
                for (int index = worker; index < pages; index += Workers)
                {
                    byte[] payload = Enumerable.Repeat((byte)(index + 1), index + 1).ToArray();
                    Assert.Equal(
                        ReserveInFlightResult.Reserved,
                        _spool.TryReserveInFlight(
                            session,
                            index,
                            payload.Length,
                            out long generation
                        )
                    );
                    Assert.Equal(
                        CommitPageResult.Committed,
                        WritePage(session, Identity, Engine, index, payload)
                    );
                    _spool.ReleaseInFlight(session, generation, payload.Length);
                }
            }
        );

        Assert.True(_spool.IsComplete(session));
        // Every reservation was released: a leak here would refuse legitimate uploads later on.
        Assert.Equal(0, ReadUnderGate(session, s => s.InFlightByGeneration.Values.Sum()));
        Assert.Equal(
            Enumerable.Range(0, pages).Sum(i => (long)(i + 1)),
            _spool.GetTotalBytes(session)
        );

        // Contended reservations of an uncommitted page: a refused reservation must not be accounted.
        const int contendedTaskId = 205;
        long half = PageStreamSpool.MaxTaskBytes / 2;
        PageStreamSession contended = _spool.GetOrCreateSession(
            contendedTaskId,
            Identity,
            Engine,
            1
        );
        var granted = new ConcurrentQueue<long>();

        RunConcurrently(
            4,
            _ =>
            {
                if (
                    _spool.TryReserveInFlight(contended, 0, half, out long generation)
                    == ReserveInFlightResult.Reserved
                )
                {
                    granted.Enqueue(generation);
                }
            }
        );

        Assert.Equal(2, granted.Count);
        Assert.Equal(2 * half, ReadUnderGate(contended, s => s.InFlightByGeneration.Values.Sum()));

        foreach (long generation in granted)
        {
            _spool.ReleaseInFlight(contended, generation, half);
        }

        Assert.Equal(0, ReadUnderGate(contended, s => s.InFlightByGeneration.Values.Sum()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentIdentityChurn_NeverMixesPagesFromDifferentIdentities()
    {
        const int taskId = 206;
        const int pageCount = 8;
        const int churners = 8;
        string[] identities = { IdentityA, IdentityB };
        var violations = new ConcurrentBag<string>();
        int activeChurners = churners;
        // The checker thread can be scheduled before the first churner has created the session, so only
        // a session that existed and then went away is a violation. The counter makes that ordering
        // happen every run rather than only when the scheduler feels like it.
        bool sawSession = false;
        int checkerLaps = 0;

        // One extra thread inspects the session throughout the churn: every page it sees recorded as
        // completed must carry bytes produced under the identity the session currently holds.
        RunConcurrently(
            churners + 1,
            worker =>
            {
                if (worker == churners)
                {
                    // Take the first look before any churner creates the session — the ordering CI hit —
                    // so "not created yet" is exercised on every run.
                    Inspect();
                    Interlocked.Increment(ref checkerLaps);

                    while (Volatile.Read(ref activeChurners) > 0)
                    {
                        Inspect();
                        Thread.Yield();
                    }

                    Inspect();
                    return;
                }

                // Wait for that first look so the ordering above is guaranteed rather than likely.
                while (Volatile.Read(ref checkerLaps) == 0)
                {
                    Thread.Yield();
                }

                try
                {
                    for (int iteration = 0; iteration < 100; iteration++)
                    {
                        // Alternating per iteration, so the identity keeps changing under the writers.
                        string identity = identities[(worker + iteration) % identities.Length];
                        PageStreamSession session = _spool.GetOrCreateSession(
                            taskId,
                            identity,
                            Engine,
                            pageCount
                        );
                        int index = (worker * 7 + iteration) % pageCount;
                        byte[] payload = new byte[16];
                        payload[0] = MarkerFor(identity);
                        payload[1] = (byte)(worker + 1);

                        // The real upload path commits under the identity it resolved from the manifest; a
                        // reset in between must drop the page, never mix it into the new identity.
                        CommitPageResult result = WritePage(
                            session,
                            identity,
                            Engine,
                            index,
                            payload
                        );
                        if (
                            result
                            is not (CommitPageResult.Committed or CommitPageResult.IdentityMismatch)
                        )
                        {
                            violations.Add($"unexpected commit result {result}");
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeChurners);
                }

                void Inspect()
                {
                    PageStreamSession? current = _spool.TryGetSession(taskId);
                    if (current is null)
                    {
                        // Not created yet (the checker can win the race to start), or the spool was
                        // replaced between the read and here. Only a session that existed and then
                        // vanished is a violation, and its absence is asserted after the churn.
                        if (sawSession)
                        {
                            violations.Add("the session disappeared during identity churn");
                        }

                        return;
                    }

                    sawSession = true;

                    lock (current.Gate)
                    {
                        byte expected = MarkerFor(current.Identity);
                        foreach (int index in current.Completed)
                        {
                            string path = current.PagePath(index);
                            if (!File.Exists(path))
                            {
                                violations.Add(
                                    $"page {index} is recorded as completed but has no file (identity {current.Identity})"
                                );
                                continue;
                            }

                            byte[] bytes = File.ReadAllBytes(path);
                            if (bytes.Length == 0 || bytes[0] != expected)
                            {
                                violations.Add(
                                    $"page {index} holds identity {bytes.ElementAtOrDefault(0)} bytes while the session is {current.Identity}"
                                );
                            }
                        }
                    }
                }
            }
        );

        Assert.True(violations.IsEmpty, string.Join(Environment.NewLine, violations.Take(20)));

        PageStreamSession final = _spool.GetOrCreateSession(taskId, IdentityA, Engine, pageCount);
        Assert.False(_spool.IsFinalized(final));
        Assert.Same(final, _spool.TryGetSession(taskId));
        // The churn must actually have reset the session, or the mixed-identity check was vacuous.
        Assert.True(ReadUnderGate(final, s => s.Generation > 1));

        // The spool is still intact and usable under the final identity.
        RunConcurrently(
            pageCount,
            worker =>
            {
                Assert.Equal(
                    CommitPageResult.Committed,
                    WritePage(final, IdentityA, Engine, worker, new byte[] { MarkerFor(IdentityA) })
                );
            }
        );

        Assert.True(_spool.IsComplete(final));
        for (int index = 0; index < pageCount; index++)
        {
            Assert.Equal(MarkerFor(IdentityA), File.ReadAllBytes(final.PagePath(index))[0]);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentManifestsAndRemovals_NeverSurfaceAnUnusableSession()
    {
        const int taskId = 207;
        const int pageCount = 4;
        const int uploaders = 6;
        const int removers = 3;
        var violations = new ConcurrentBag<string>();

        RunConcurrently(
            uploaders + removers,
            worker =>
            {
                if (worker >= uploaders)
                {
                    for (int i = 0; i < 50; i++)
                    {
                        _spool.Remove(taskId);
                    }

                    return;
                }

                for (int iteration = 0; iteration < 50; iteration++)
                {
                    PageStreamSession session = _spool.GetOrCreateSession(
                        taskId,
                        Identity,
                        Engine,
                        pageCount
                    );
                    int index = iteration % pageCount;
                    byte[] payload = new byte[] { (byte)(iteration + 1) };

                    FileStream output;
                    string temp;
                    try
                    {
                        output = _spool.BeginPageWrite(session, index, out temp);
                    }
                    catch (PageStreamRestartException ex)
                    {
                        // The session was finalized between the manifest and the write. That has to be the
                        // restart signal — never a raw DirectoryNotFound/FileNotFound from a deleted spool.
                        if (!ex.ResetSpool)
                        {
                            violations.Add(
                                "a finalized session reported a restart without a spool reset"
                            );
                        }

                        continue;
                    }

                    using (output)
                    {
                        output.Write(payload);
                    }

                    CommitPageResult result = _spool.TryCommitPage(
                        session,
                        Identity,
                        Engine,
                        index,
                        temp,
                        payload.Length
                    );
                    if (result != CommitPageResult.Committed)
                    {
                        TryDeleteTemp(temp);
                    }

                    // A page racing a concurrent remove is dropped, not accounted and not fatal.
                    if (
                        result
                        is not (CommitPageResult.Committed or CommitPageResult.IdentityMismatch)
                    )
                    {
                        violations.Add($"unexpected commit result {result} during removal churn");
                    }
                }
            }
        );

        Assert.True(violations.IsEmpty, string.Join(Environment.NewLine, violations.Take(20)));

        // Once the churn stops, the task must get a usable spool again rather than a tombstone.
        _spool.Remove(taskId);
        PageStreamSession fresh = _spool.GetOrCreateSession(taskId, Identity, Engine, pageCount);
        Assert.False(_spool.IsFinalized(fresh));
        Assert.Same(fresh, _spool.TryGetSession(taskId));
        Assert.Equal(
            CommitPageResult.Committed,
            WritePage(fresh, Identity, Engine, 0, new byte[] { 5, 6 })
        );
        Assert.True(File.Exists(fresh.PagePath(0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConcurrentAssemblyClaims_HaveExactlyOneWinner()
    {
        const int taskId = 208;
        PageStreamSession session = _spool.GetOrCreateSession(taskId, Identity, Engine, 1);
        int winners = 0;

        RunConcurrently(
            16,
            _ =>
            {
                if (_spool.TryBeginAssembly(session, Identity))
                {
                    Interlocked.Increment(ref winners);
                }
            }
        );

        Assert.Equal(1, winners);
        // The claim stays exclusive until the finalizer releases it.
        Assert.False(_spool.TryBeginAssembly(session, Identity));
        _spool.EndAssembly(session);
        Assert.True(_spool.TryBeginAssembly(session, Identity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SweepStale_ConcurrentWithActivity_KeepsLiveSessionsAndDropsIdleOnes()
    {
        const int liveTaskId = 209;
        const int recentTaskId = 210;
        const int idleTaskId = 211;

        PageStreamSession live = _spool.GetOrCreateSession(liveTaskId, Identity, Engine, 4);
        Assert.Equal(
            CommitPageResult.Committed,
            WritePage(live, Identity, Engine, 0, new byte[] { 1 })
        );

        // Older than the idle-reclaim threshold in spirit, but inside the retention window: a boundary
        // case that a correct sweep must keep.
        PageStreamSession recent = _spool.GetOrCreateSession(recentTaskId, Identity, Engine, 1);
        Assert.Equal(
            CommitPageResult.Committed,
            WritePage(recent, Identity, Engine, 0, new byte[] { 2 })
        );
        recent.LastTouchedUtc = DateTime.UtcNow - TimeSpan.FromMinutes(30);

        PageStreamSession idle = _spool.GetOrCreateSession(idleTaskId, Identity, Engine, 1);
        Assert.Equal(
            CommitPageResult.Committed,
            WritePage(idle, Identity, Engine, 0, new byte[] { 3 })
        );
        idle.LastTouchedUtc = DateTime.UtcNow - TimeSpan.FromHours(48);

        RunConcurrently(
            4,
            worker =>
            {
                if (worker == 0)
                {
                    for (int i = 0; i < 50; i++)
                    {
                        _spool.SweepStale(SweepRetention);
                        Thread.Yield();
                    }

                    return;
                }

                for (int i = 0; i < 200; i++)
                {
                    PageStreamSession session = _spool.GetOrCreateSession(
                        liveTaskId,
                        Identity,
                        Engine,
                        4
                    );
                    Assert.Equal(
                        CommitPageResult.Committed,
                        WritePage(session, Identity, Engine, 1, new byte[] { 4 })
                    );
                    Thread.Yield();
                }
            }
        );

        // A session kept fresh by concurrent activity must survive the sweeps...
        Assert.Same(live, _spool.TryGetSession(liveTaskId));
        Assert.False(_spool.IsFinalized(live));
        Assert.True(Directory.Exists(live.Directory));
        Assert.Same(recent, _spool.TryGetSession(recentTaskId));
        Assert.False(recent.Finalized);

        // ...while one that has been idle past the retention window is reclaimed.
        Assert.Null(_spool.TryGetSession(idleTaskId));
        Assert.True(idle.Finalized);
        Assert.False(Directory.Exists(idle.Directory));
    }

    /// <summary>
    /// Writes and commits one page the way the server's upload handler does: stream to a temp file,
    /// then commit under the identity resolved from the manifest, cleaning up on rejection.
    /// </summary>
    private CommitPageResult WritePage(
        PageStreamSession session,
        string expectedIdentity,
        string expectedEngineIdentity,
        int pageIndex,
        byte[] payload
    )
    {
        string temp;
        using (FileStream output = _spool.BeginPageWrite(session, pageIndex, out temp))
        {
            output.Write(payload);
        }

        CommitPageResult result = _spool.TryCommitPage(
            session,
            expectedIdentity,
            expectedEngineIdentity,
            pageIndex,
            temp,
            payload.Length
        );
        if (result != CommitPageResult.Committed)
        {
            TryDeleteTemp(temp);
        }

        return result;
    }

    /// <summary>
    /// Best-effort temp cleanup, mirroring the server's own: an identity reset can delete the session
    /// directory — and the temp file inside it — before the commit is rejected.
    /// </summary>
    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
            // The session directory was already reclaimed; there is nothing left to clean up.
        }
    }

    /// <summary>
    /// Runs <paramref name="workerCount"/> bodies on dedicated threads released together, so the
    /// operations genuinely overlap. A thread-pool start gate would serialize on a starved pool
    /// (the pool grows only after it notices blocked work), which would weaken every assertion here.
    /// </summary>
    private static void RunConcurrently(int workerCount, Action<int> body)
    {
        using var start = new ManualResetEventSlim(false);
        var failures = new ConcurrentQueue<Exception>();
        var threads = new Thread[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int worker = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    body(worker);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })
            {
                IsBackground = true,
                Name = $"spool-concurrency-{worker}",
            };
            threads[i].Start();
        }

        start.Set();

        foreach (Thread thread in threads)
        {
            Assert.True(
                thread.Join(TimeSpan.FromMinutes(1)),
                "A spool concurrency worker did not finish."
            );
        }

        if (!failures.IsEmpty)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// Reads mutable session state the way the spool itself does — under the session gate — instead of
    /// racing a writer that is mid-update.
    /// </summary>
    private static T ReadUnderGate<T>(PageStreamSession session, Func<PageStreamSession, T> read)
    {
        lock (session.Gate)
        {
            return read(session);
        }
    }

    private static string PageFileName(int pageIndex) => $"page_{pageIndex:D5}.bin";

    /// <summary>Payload marker that identifies the engine that produced a page.</summary>
    private static byte MarkerFor(string identity) => identity == IdentityA ? (byte)1 : (byte)2;
}

/// <summary>
/// The spool sweeps the shared process-global spool parent, so its test classes must not run in
/// parallel: one class deliberately ages a live root while another calls SweepStale, and a parallel
/// sweeper would reclaim the other's live root nondeterministically.
/// </summary>
[CollectionDefinition("PageStreamSpoolSharedRoot", DisableParallelization = true)]
public class PageStreamSpoolSharedRootCollection;
