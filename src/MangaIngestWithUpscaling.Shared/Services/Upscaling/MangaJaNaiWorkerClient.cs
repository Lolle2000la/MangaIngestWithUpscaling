using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Processes;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Owns a long-running <c>worker.py</c> subprocess and routes upscale jobs to it over NDJSON.
/// The process is spawned lazily on the first job, kept alive so models/GPU stay warm across jobs,
/// and torn down by the inherited idle watchdog once it has been idle for
/// <see cref="UpscalerConfig.WorkerIdleTimeout"/>.
/// </summary>
public class MangaJaNaiWorkerClient : ResidentNdjsonProcess, IMangaJaNaiWorkerClient
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long the polite shutdown command waits for the stdin lock before the caller falls back
    ///     to killing the process. A wedged worker's page producer can hold that lock while blocked on
    ///     a full pipe, so an unbounded wait would hang host shutdown.
    /// </summary>
    private static readonly TimeSpan ShutdownSendGracePeriod = TimeSpan.FromSeconds(2);

    // After every page is upscaled and saved, the only remaining work is finalizing the
    // output archive, which produces no progress events. Give it a fixed grace period
    // instead of the pixel-scaled inactivity timeout.
    private static readonly TimeSpan PostprocessGracePeriod = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, WorkerJob> _jobs = new();

    private string? _currentJobId;

    public MangaJaNaiWorkerClient(
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> config,
        ILogger<MangaJaNaiWorkerClient> logger,
        IHostApplicationLifetime lifetime
    )
        : base(config, logger, lifetime, ReadyTimeout)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<UpscaleJobResult> RunJobAsync(
        UpscaleJobRequest request,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        await _submitLock.WaitAsync(cancellationToken);
        try
        {
            // Touch the activity clock before spawning so the idle watchdog doesn't
            // race a fresh job submission and tear the worker down mid-spawn.
            TouchActivity();

            await EnsureWorkerAsync(cancellationToken);

            WorkerJob job = new(request.Id, progress);
            if (!_jobs.TryAdd(request.Id, job))
            {
                throw new InvalidOperationException(
                    $"A job with id '{request.Id}' is already in flight."
                );
            }

            _currentJobId = request.Id;

            try
            {
                try
                {
                    await SendLineAsync(BuildJobLine(request), cancellationToken);
                }
                catch (Exception ex)
                    when (!cancellationToken.IsCancellationRequested
                        && (
                            (ex is IOException or ObjectDisposedException)
                            || (ex is InvalidOperationException && _stdin is null)
                        )
                    )
                {
                    // The worker crashed between the ready check and submission; respawn once
                    // with a fresh job and retry. A broken pipe surfaces as IOException/
                    // ObjectDisposedException rather than InvalidOperationException.
                    _logger.LogWarning(
                        "Upscale worker crashed during submission; respawning and retrying once."
                    );
                    _jobs.TryRemove(request.Id, out _);
                    // Publish the replacement process before registering the replacement job: the
                    // dead process's OnWorkerExited faults the jobs it snapshots, and it must not see
                    // (and fault) a job that belongs to the new worker.
                    await EnsureWorkerAsync(cancellationToken);
                    job = new WorkerJob(request.Id, progress);
                    _jobs.TryAdd(request.Id, job);
                    await SendLineAsync(BuildJobLine(request), cancellationToken);
                }

                var cancelSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                using var cancelReg = cancellationToken.Register(() =>
                {
                    cancelSignal.TrySetResult();
                    _ = RequestCancelAsync(request.Id);
                });

                // Only monitor inactivity when a timeout was supplied; a null timeout would
                // otherwise leave an infinite, never-completing task alive for the process.
                Task? monitor = timeout is null ? null : MonitorTimeoutAsync(job, timeout);

                List<Task> waiters = [job.Completion.Task, cancelSignal.Task];
                if (monitor is not null)
                {
                    waiters.Add(monitor);
                }

                Task completed = await Task.WhenAny(waiters);

                if (completed == cancelSignal.Task)
                {
                    // Wait for the worker to release the job slot, then surface cancellation.
                    try
                    {
                        await job.Completion.Task.WaitAsync(
                            CancelGracePeriod,
                            CancellationToken.None
                        );
                    }
                    catch (Exception)
                    { /* the worker may already be gone; cancellation wins */
                    }

                    // If the worker didn't acknowledge the cancel in time, kill it so a stuck
                    // job doesn't occupy the only worker slot for the next submission.
                    if (!job.Completion.Task.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Upscale worker job {JobId} did not cancel in time; killing the worker.",
                            request.Id
                        );
                        await KillWorkerAsync();
                    }

                    throw new OperationCanceledException(cancellationToken);
                }

                // Prefer the monitor when it requested the timeout, even if the job's own completion
                // won the race: a worker that honors the cancel emits a "cancelled" done, which
                // faults the job with a non-timeout error that would otherwise classify as a hard
                // failure and delete the spool. The timeout is the authoritative outcome.
                if (monitor is not null && (completed == monitor || job.TimeoutRequested))
                {
                    await monitor; // throws TimeoutException after escalating cancel/kill
                }

                return await job.Completion.Task;
            }
            finally
            {
                _jobs.TryRemove(request.Id, out _);
                _currentJobId = null;
                TouchActivity();
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    public async Task<UpscaleJobResult> RunChapterAsync(
        ChapterJobRequest request,
        IAsyncEnumerable<ChapterPage> pages,
        IProgress<UpscaleProgress>? progress,
        Action<UpscaleJobFile> onPageDone,
        CancellationToken cancellationToken,
        TimeSpan? timeout
    )
    {
        await _submitLock.WaitAsync(cancellationToken);
        try
        {
            TouchActivity();
            await EnsureWorkerAsync(cancellationToken);

            WorkerJob job = new(request.Id, progress, onPageDone, failOnPageErrors: false);
            if (!_jobs.TryAdd(request.Id, job))
            {
                throw new InvalidOperationException(
                    $"A job with id '{request.Id}' is already in flight."
                );
            }

            _currentJobId = request.Id;

            try
            {
                try
                {
                    await SendLineAsync(BuildChapterLine(request), cancellationToken);
                }
                catch (Exception ex)
                    when (!cancellationToken.IsCancellationRequested
                        && (
                            (ex is IOException or ObjectDisposedException)
                            || (ex is InvalidOperationException && _stdin is null)
                        )
                    )
                {
                    _logger.LogWarning(
                        "Upscale worker crashed during chapter submission; respawning and retrying once."
                    );
                    _jobs.TryRemove(request.Id, out _);
                    // Publish the replacement process before registering the replacement job: the
                    // dead process's OnWorkerExited faults the jobs it snapshots, and it must not see
                    // (and fault) a job that belongs to the new worker.
                    await EnsureWorkerAsync(cancellationToken);
                    job = new WorkerJob(request.Id, progress, onPageDone, failOnPageErrors: false);
                    _jobs.TryAdd(request.Id, job);
                    await SendLineAsync(BuildChapterLine(request), cancellationToken);
                }

                using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                Exception? producerError = null;
                // Completes only on a producer *failure*, not on its normal finish: the producer
                // finishes before the job does, so waiting on the producer task itself would settle the
                // chapter early. A failure must unblock the wait, though — otherwise a wedged worker
                // that ignores the cancel leaves the job, the cancel signal and the (absent) timeout
                // all pending, hanging the job and leaking the single worker slot forever.
                var producerFault = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                Task producer = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await foreach (
                                ChapterPage page in pages.WithCancellation(producerCts.Token)
                            )
                            {
                                await SendLineAsync(
                                    BuildPageLine(request.Id, page),
                                    producerCts.Token
                                );
                            }

                            await SendLineAsync(
                                BuildCloseChapterLine(request.Id),
                                producerCts.Token
                            );
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled by us (the chapter settled) or by the caller; not an error.
                        }
                        catch (Exception ex)
                        {
                            producerError = ex;
                            producerFault.TrySetResult();
                            await RequestCancelAsync(request.Id);
                        }
                    },
                    CancellationToken.None
                );

                var cancelSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                using CancellationTokenRegistration cancelReg = cancellationToken.Register(() =>
                {
                    cancelSignal.TrySetResult();
                    _ = RequestCancelAsync(request.Id);
                });

                // Only monitor inactivity when a timeout was supplied; a null timeout would
                // otherwise leave an infinite, never-completing task alive for the process.
                Task? monitor = timeout is null ? null : MonitorTimeoutAsync(job, timeout);

                List<Task> waiters = [job.Completion.Task, cancelSignal.Task, producerFault.Task];
                if (monitor is not null)
                {
                    waiters.Add(monitor);
                }

                Task completed = await Task.WhenAny(waiters);

                // Stop feeding pages as soon as the chapter settles (finished, cancelled or timed
                // out). On the timeout path the cancellation token is not signalled, so this is the
                // only thing that stops the producer.
                await producerCts.CancelAsync();
                try
                {
                    await producer.WaitAsync(CancelGracePeriod, CancellationToken.None);
                }
                catch (Exception)
                { /* the producer stops with the chapter */
                }

                if (completed == cancelSignal.Task)
                {
                    try
                    {
                        await job.Completion.Task.WaitAsync(
                            CancelGracePeriod,
                            CancellationToken.None
                        );
                    }
                    catch (Exception)
                    { /* the worker may already be gone; cancellation wins */
                    }

                    if (!job.Completion.Task.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Upscale worker chapter {JobId} did not cancel in time; killing the worker.",
                            request.Id
                        );
                        await KillWorkerAsync();
                    }

                    throw new OperationCanceledException(cancellationToken);
                }

                // Prefer the monitor when it requested the timeout, even if the job's own completion
                // won the race: a worker that honors the cancel emits a "cancelled" done, which
                // faults the job with a non-timeout error that would otherwise classify as a hard
                // failure and delete the spool. The timeout is the authoritative outcome.
                if (monitor is not null && (completed == monitor || job.TimeoutRequested))
                {
                    await monitor; // throws TimeoutException after escalating cancel/kill
                }

                // Surface a worker *crash* (exit code + captured stderr) rather than the producer's
                // consequential broken-pipe error. A producer failure that merely induced a
                // "cancelled" done is not a crash, so the producer error is the real cause there.
                if (job.WorkerExited)
                {
                    if (producerError is not null)
                    {
                        _logger.LogWarning(
                            producerError,
                            "Streaming chapter {JobId} failed while the worker process exited; surfacing the worker error.",
                            request.Id
                        );
                    }

                    return await job.Completion.Task;
                }

                if (completed == producerFault.Task)
                {
                    // The producer failed (broken pipe or a page-source error). Give the worker the
                    // same grace as a caller cancellation and kill it if it does not stop, so a wedged
                    // process that ignores the cancel does not hold the single worker slot until host
                    // restart. The producer error is surfaced below.
                    try
                    {
                        await job.Completion.Task.WaitAsync(
                            CancelGracePeriod,
                            CancellationToken.None
                        );
                    }
                    catch (Exception)
                    { /* the worker may already be gone */
                    }

                    if (!job.Completion.Task.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Upscale worker chapter {JobId} did not stop after a streaming failure; killing the worker.",
                            request.Id
                        );
                        await KillWorkerAsync();
                    }
                }

                if (producerError is not null)
                {
                    throw new InvalidOperationException(
                        "Failed to stream the chapter pages to the upscale worker.",
                        producerError
                    );
                }

                return await job.Completion.Task;
            }
            finally
            {
                _jobs.TryRemove(request.Id, out _);
                _currentJobId = null;
                TouchActivity();
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    public Task ShutdownWorkerAsync(CancellationToken cancellationToken) =>
        ShutdownWorkerAsync(force: false, cancellationToken);

    /// <summary>
    /// Tears down the worker. <paramref name="force"/> skips the in-flight guard: host shutdown uses
    /// it, since a job that has not yet observed ApplicationStopping must not keep the Python worker
    /// (and its GPU memory) alive.
    /// </summary>
    public async Task ShutdownWorkerAsync(bool force, CancellationToken cancellationToken)
    {
        Process? process;
        StreamWriter? stdin;
        bool alreadyExited;
        lock (_stateLock)
        {
            // Never tear down the worker while a job is in flight: a streamed chapter (or a
            // whole-CBZ job) holds it, and killing it would fail that task. The next detection
            // attempt after the job finishes shuts it down instead.
            if (!force && (_currentJobId is not null || !_jobs.IsEmpty))
            {
                _logger.LogDebug(
                    "Not shutting down the upscale worker: a job is in flight or queued."
                );
                return;
            }

            process = _process;
            stdin = _stdin;
            _shuttingDown = true;
            alreadyExited = process is null || process.HasExited;
            if (alreadyExited)
            {
                _process = null;
            }
        }

        if (alreadyExited)
        {
            // The process already exited; dispose the captured stdin rather than leak it.
            await DisposeStdinAsync(stdin);
            return;
        }

        try
        {
            // Write to the captured stdin, not the shared _stdin field, so a concurrent
            // spawn can't make us shut down the *new* worker by mistake.
            if (stdin is not null)
            {
                // Best-effort and bounded: a wedged worker's page producer can hold the stdin lock
                // while blocked on a full pipe, so waiting on it forever would hang host shutdown.
                // Fall through to the kill below.
                using var sendGrace = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                sendGrace.CancelAfter(ShutdownSendGracePeriod);
                try
                {
                    await SendLineAsync(
                        stdin,
                        JsonSerializer.Serialize(new WorkerCommand("shutdown"), WorkerJson.Options),
                        sendGrace.Token
                    );
                }
                catch (OperationCanceledException) { }
            }

            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                // alreadyExited is false here, so process is non-null.
                await process!.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while shutting down the upscale worker.");
        }

        await CleanupAsync(process);
    }

    /// <summary>
    /// Asks the running worker to return its cached allocator blocks (VRAM) to the driver so
    /// co-tenant GPU processes (e.g. split detection) can run while the worker stays warm.
    /// Skipped when no worker is running or a job is in flight (freeing cached blocks mid-job
    /// would only slow the running inference).
    /// </summary>
    public async Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken)
    {
        Process? process;
        TaskCompletionSource? tcs;
        lock (_stateLock)
        {
            process = _process;
            if (process is null || process.HasExited)
            {
                return false;
            }

            if (_currentJobId is not null || !_jobs.IsEmpty)
            {
                _logger.LogDebug("Not releasing worker GPU cache: a job is in flight or queued.");
                return false;
            }

            if (_cacheReleaseTcs is not null)
            {
                _logger.LogDebug("Not releasing worker GPU cache: a release is already in flight.");
                return false;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cacheReleaseTcs = tcs;
        }

        try
        {
            try
            {
                await SendLineAsync(
                    JsonSerializer.Serialize(
                        new WorkerCommand("release_cache"),
                        WorkerJson.Options
                    ),
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send the GPU cache release request to the worker.");
                return false;
            }

            // A healthy idle worker answers immediately; the timeout only guards against a
            // wedged process. The worker may legitimately answer "busy" (job started in the
            // meantime), which still counts as an acknowledged reply.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The upscale worker did not acknowledge the GPU cache release.");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            // Clear on every exit path: a failed send must not leave the in-flight guard set for the
            // singleton's lifetime, which would silently disable all later releases.
            lock (_stateLock)
            {
                if (_cacheReleaseTcs == tcs)
                {
                    _cacheReleaseTcs = null;
                }
            }
        }
    }

    protected override Task ShutdownProcessAsync(bool force, CancellationToken cancellationToken) =>
        ShutdownWorkerAsync(force, cancellationToken);

    protected override bool HasInFlightJob => _currentJobId is not null || !_jobs.IsEmpty;

    protected override void HandleStdoutLine(string line) => HandleEvent(line);

    protected override Exception CreateStdinUnavailableException() =>
        new InvalidOperationException("Upscale worker stdin is not available.");

    protected override Exception CreateProcessStartFailureException(Exception? inner) =>
        new InvalidOperationException("Failed to start the upscale worker process.");

    /// <summary>
    /// Asks the resident detection server to return its cached VRAM before the upscaler claims the
    /// GPU, so two warm models do not fight over a small card. Best-effort.
    /// </summary>
    private async Task ReleaseDetectorGpuAsync()
    {
        try
        {
            using IServiceScope detectorScope = _scopeFactory.CreateScope();
            IDetectServerClient? detector =
                detectorScope.ServiceProvider.GetService<IDetectServerClient>();
            if (detector is not null)
            {
                await detector.ReleaseGpuCacheAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to release the detection server's GPU cache before upscaling."
            );
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        // Free the resident detection server's VRAM before the upscaler claims the GPU, so two warm
        // models do not fight over a small card. Done before the warm-worker early return, so a
        // detection run between two upscale jobs still releases the detector. Best-effort.
        await ReleaseDetectorGpuAsync();

        Process? existing;
        lock (_stateLock)
        {
            existing = _process;
            if (
                existing is not null
                && !existing.HasExited
                && !_shuttingDown
                && _readyTcs?.Task.IsCompletedSuccessfully == true
            )
            {
                return;
            }
        }

        // A previous (dead or shutdown) process may still be around; dispose it.
        if (existing is not null)
        {
            await CleanupAsync(existing);
        }

        // Start a fresh stderr buffer for the new worker so a timeout/crash report doesn't
        // include diagnostics from the previous process.
        ClearStderr();

        // IPythonService is scoped (it owns per-request GPU detection), so resolve it from a
        // short-lived scope here instead of injecting it into this singleton.
        PythonEnvironment? environment;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            environment = scope
                .ServiceProvider.GetRequiredService<IPythonService>()
                .GetPreparedEnvironment();
        }

        if (environment is null)
        {
            throw new InvalidOperationException(
                "Python environment is not initialized. Call PreparePythonEnvironment first."
            );
        }

        string settingsPath = MangaJaNaiWorkerSettings.EnsureSettings(_config.Value);

        // Explicit no-BOM UTF-8 so the first JSON line is not prefixed with a BOM.
        ProcessStartInfo startInfo = CreateStartInfo(
            environment,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        startInfo.ArgumentList.Add("worker.py");
        startInfo.ArgumentList.Add("--settings");
        startInfo.ArgumentList.Add(settingsPath);
        startInfo.ArgumentList.Add("--queue-capacity");
        startInfo.ArgumentList.Add(_config.Value.WorkerQueueCapacity.ToString());
        // Exit the worker when this process dies, so an abruptly killed host does not leave a warm
        // model (and its GPU memory) resident forever.
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        if (_config.Value.WorkerIdleCacheReleaseTimeout > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("--cache-release-idle");
            startInfo.ArgumentList.Add(
                _config.Value.WorkerIdleCacheReleaseTimeout.TotalSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        }
        if (_config.Value.WorkerWarmup)
        {
            startInfo.ArgumentList.Add("--warmup");
        }

        ApplyPythonEnvironmentVariables(startInfo);

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int processId = StartProcess(startInfo, readyTcs);

        using var timeoutCts = new CancellationTokenSource(ReadyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token
        );
        try
        {
            await WaitForReadyAsync(readyTcs, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            string stderrSection = BuildStderrSection();
            await KillWorkerAsync();
            // TimeoutException (not InvalidOperationException) so the streaming classifier treats a
            // slow cold start as recoverable rather than dropping the already-spooled pages.
            throw new TimeoutException(
                $"Timed out waiting for the upscale worker to become ready.{stderrSection}"
            );
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled while we were waiting for ready; tear down the spawned
            // worker so it doesn't linger until the next job or shutdown.
            await KillWorkerAsync();
            throw;
        }

        TouchActivity();
        _logger.LogInformation(
            "Upscale worker started (pid {Pid}) using settings {SettingsPath}.",
            processId,
            settingsPath
        );
    }

    private async Task KillWorkerAsync()
    {
        Process? process;
        TaskCompletionSource? cacheRelease;
        lock (_stateLock)
        {
            process = _process;
            _shuttingDown = true;
            cacheRelease = _cacheReleaseTcs;
            _cacheReleaseTcs = null;
        }

        cacheRelease?.TrySetCanceled();

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing the upscale worker.");
        }

        await CleanupAsync(process);
    }

    private async Task CleanupAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        StreamWriter? stdin;
        WorkerJob[] jobs;
        lock (_stateLock)
        {
            if (_process == process)
            {
                _process = null;
                _readyTcs = null;
                stdin = _stdin;
                _stdin = null;
                // Fault any in-flight jobs here too: a forced shutdown can null _process before the
                // stdout reader observes EOF, so OnWorkerExited would early-return and leave a job's
                // Completion uncompleted, hanging its caller forever.
                jobs = _jobs.Values.ToArray();
            }
            else
            {
                // A newer worker has already replaced this one; leave its stdin and jobs alone.
                stdin = null;
                jobs = [];
            }
        }

        // Kill before waiting on the stdin lock: a page producer blocked writing to a wedged worker
        // holds that lock, so disposing stdin first could wait forever and the process would never be
        // killed. Killing closes the pipe and releases the producer.
        await KillAndDisposeAsync(process);
        await DisposeStdinAsync(stdin);
        foreach (WorkerJob job in jobs)
        {
            job.MarkWorkerExited();
            job.FailCrashed("Upscale worker was shut down while the job was in flight.");
        }

        _logger.LogInformation("Upscale worker process stopped.");
    }

    internal static string BuildJobLine(UpscaleJobRequest request)
    {
        var job = new WorkerJobRequest
        {
            Id = request.Id,
            Input = new WorkerJobInput { Path = request.InputPath },
            Output = new WorkerJobOutput
            {
                Folder = request.OutputFolder,
                Filename = request.OutputFilename,
                Format = ToFormatString(request.Format),
                Overwrite = request.Overwrite,
                Quality = request.Quality,
            },
            Options = new WorkerJobOptions { Scale = (int)request.Scale },
        };
        return JsonSerializer.Serialize(job, WorkerJson.Options);
    }

    internal static string BuildChapterLine(ChapterJobRequest request)
    {
        var chapter = new WorkerChapterRequest
        {
            Id = request.Id,
            Output = new WorkerJobOutput
            {
                Folder = request.OutputFolder,
                Filename = "%filename%",
                Format = ToFormatString(request.Format),
                Overwrite = true,
                Quality = request.Quality,
            },
            Options = new WorkerJobOptions { Scale = (int)request.Scale },
            TotalPages = request.TotalPages,
        };
        return JsonSerializer.Serialize(chapter, WorkerJson.Options);
    }

    internal static string BuildPageLine(string id, ChapterPage page)
    {
        var request = new WorkerPageRequest
        {
            Id = id,
            Index = page.Index,
            Name = page.Name,
            Path = page.Path,
        };
        return JsonSerializer.Serialize(request, WorkerJson.Options);
    }

    internal static string BuildCloseChapterLine(string id)
    {
        var request = new WorkerCloseChapterRequest { Id = id };
        return JsonSerializer.Serialize(request, WorkerJson.Options);
    }

    internal static string ToFormatString(CompressionFormat format) =>
        format switch
        {
            CompressionFormat.Webp => "webp",
            CompressionFormat.Png => "png",
            CompressionFormat.Jpg => "jpeg",
            CompressionFormat.Avif => "avif",
            _ => "webp",
        };

    private async Task RequestCancelAsync(string jobId)
    {
        try
        {
            await SendLineAsync(
                JsonSerializer.Serialize(new WorkerCommand("cancel", jobId), WorkerJson.Options),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send cancel for job {JobId}.", jobId);
        }
    }

    private void HandleEvent(string line)
    {
        try
        {
            WorkerEvent? evt = JsonSerializer.Deserialize<WorkerEvent>(line, WorkerJson.Options);
            switch (evt)
            {
                case WorkerReadyEvent:
                    SignalReady();
                    break;
                case WorkerProgressEvent progress:
                    DispatchProgress(progress);
                    break;
                case WorkerDoneEvent done:
                    DispatchDone(done);
                    break;
                case WorkerPageDoneEvent pageDone:
                    DispatchPageDone(pageDone);
                    break;
                case WorkerErrorEvent error:
                    DispatchError(error);
                    break;
                case WorkerRejectedEvent rejected:
                    DispatchRejected(rejected);
                    break;
                case WorkerAcceptedEvent accepted:
                    // Reset the per-job inactivity clock when the worker acknowledges a job,
                    // so the model-loading gap before the first progress event isn't idle.
                    TouchJob(accepted.Id);
                    break;
                case WorkerStartedEvent started:
                    TouchJob(started.Id);
                    break;
                case WorkerCancelledEvent cancelled:
                    // Acknowledge the cancel; the job completes via the subsequent done event.
                    TouchJob(cancelled.Id);
                    break;
                case WorkerCacheReleasedEvent cacheReleased:
                    // "ok" released the cache; "busy" (a job is running) did not, so the caller must
                    // not be told the release succeeded.
                    HandleCacheReleased(cacheReleased.Status);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse upscale worker event: {Line}", line);
        }
    }

    private void DispatchProgress(WorkerProgressEvent progress)
    {
        if (!TryGetAndTouch(progress.Id, out WorkerJob? job))
        {
            return;
        }

        // Prefer archive_completed for archive jobs: it counts only finished archive
        // entries, so it never exceeds archive_total (completed also counts the final
        // 'archive finished' marker, which briefly showed e.g. 33/32).
        int? completed = progress.ArchiveCompleted ?? progress.Completed;
        if (progress.ArchiveTotal is > 0 && completed >= progress.ArchiveTotal)
        {
            job.MarkAllPagesProcessed();
        }

        job.ReportProgress(progress.ArchiveTotal, completed, progress.Phase);
    }

    private void DispatchDone(WorkerDoneEvent done)
    {
        if (!TryGetAndTouch(done.Id, out WorkerJob? job))
        {
            return;
        }

        string status = done.Status ?? "ok";
        var files = (done.Files ?? [])
            .Select(f => new UpscaleJobFile(f.Input ?? "", f.Output ?? "", f.Status ?? ""))
            .ToList();

        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            // A cancel the worker honored is a caller cancellation, not a failure: surface it as a
            // cancellation so it is not classified permanent and does not drop the spool.
            job.Cancel();
            return;
        }

        if (status != "ok")
        {
            job.Fail($"Upscale worker reported status '{status}'.");
            return;
        }

        string[] failed = files.Where(f => f.Status == "error").Select(f => f.Input).ToArray();
        if (job.FailOnPageErrors && failed.Length > 0)
        {
            job.Fail(
                $"Upscale worker failed to process {failed.Length} file(s): {string.Join(", ", failed)}"
            );
            return;
        }

        job.TrySetResult(new UpscaleJobResult(job.Id, status, files, done.ElapsedSeconds));
    }

    private void DispatchPageDone(WorkerPageDoneEvent pageDone)
    {
        if (!TryGetAndTouch(pageDone.Id, out WorkerJob? job))
        {
            return;
        }

        if (!string.IsNullOrEmpty(pageDone.Error))
        {
            _logger.LogWarning(
                "Upscale worker reported an error for page {Input} of job {JobId}: {Error}",
                pageDone.Input,
                pageDone.Id,
                pageDone.Error
            );
        }

        // Carry the engine's per-page message into the status so it reaches the task's persisted
        // error instead of the caller only being able to report "<input>: error".
        string status = pageDone.Status ?? "";
        if (status == "error" && !string.IsNullOrEmpty(pageDone.Error))
        {
            status = $"error: {pageDone.Error}";
        }

        try
        {
            job.OnPageDone?.Invoke(
                new UpscaleJobFile(pageDone.Input ?? "", pageDone.Output ?? "", status)
            );
        }
        catch (Exception ex)
        {
            // A throwing callback must not tear down the stdout reader (which would fault every
            // in-flight job); fail just this job instead.
            _logger.LogError(ex, "Page-done callback failed for job {JobId}.", pageDone.Id);
            job.Fail($"Page-done callback failed: {ex.Message}");
        }
    }

    private void DispatchError(WorkerErrorEvent error)
    {
        string message = error.Message ?? "";

        if (TryGetAndTouch(error.Id, out WorkerJob? job))
        {
            // Treat a worker-level error as recoverable (like a crash): it is commonly a transient
            // engine fault (e.g. a caught CUDA OOM), and reporting it as terminal would delete the
            // already-upscaled pages. The soft-failure cap still bounds a deterministic error.
            job.FailCrashed($"Upscale worker error: {message}");
            return;
        }

        _logger.LogWarning("Upscale worker reported an error: {Message}", message);
    }

    private void DispatchRejected(WorkerRejectedEvent rejected)
    {
        string reason = rejected.Reason ?? "";

        if (TryGetJob(rejected.Id, out WorkerJob? job))
        {
            job.Fail($"Upscale worker rejected the job: {reason}");
        }
    }

    protected override async Task OnProcessExitedAsync(Process process)
    {
        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch { }

        string detail = exitCode is null ? "unknown" : exitCode.Value.ToString();
        string stderrSection = BuildStderrSection();

        TaskCompletionSource? readyTcs;
        StreamWriter? stdin;
        TaskCompletionSource? cacheRelease;
        WorkerJob[] jobs;
        lock (_stateLock)
        {
            // A stale process (already replaced by a newer spawn) must not fault the new
            // worker's jobs or ready signal.
            if (_process != process)
            {
                return;
            }

            _process = null;
            readyTcs = _readyTcs;
            _readyTcs = null;
            stdin = _stdin;
            _stdin = null;
            cacheRelease = _cacheReleaseTcs;
            _cacheReleaseTcs = null;
            // Snapshot the jobs while the exiting process is still current: a crash-retry that
            // respawns the worker and adds a replacement job after this point must not have its new
            // job faulted by the dead process's exit.
            jobs = _jobs.Values.ToArray();
        }

        // Unblock a pending GPU-cache release so its in-flight guard is not left set.
        cacheRelease?.TrySetCanceled();

        foreach (WorkerJob job in jobs)
        {
            job.MarkWorkerExited();
            job.FailCrashed(
                $"Upscale worker process exited unexpectedly (exit code {detail}).{stderrSection}"
            );
        }

        readyTcs?.TrySetException(
            new UpscaleWorkerCrashedException("Upscale worker exited before becoming ready.")
        );

        // The stdout reader only reaches here on EOF, but if the process is somehow still alive (e.g.
        // it closed stdout without exiting), kill it so it isn't orphaned. Kill before waiting on the
        // stdin lock: a producer blocked writing to such a worker holds that lock, so disposing stdin
        // first could wait forever.
        await KillAndDisposeAsync(process);
        await DisposeStdinAsync(stdin);
    }

    private bool TryGetJob(string? id, [NotNullWhen(true)] out WorkerJob? job)
    {
        job = null;
        return id is not null && _jobs.TryGetValue(id, out job);
    }

    private bool TryGetAndTouch(string? id, [NotNullWhen(true)] out WorkerJob? job)
    {
        if (!TryGetJob(id, out job))
        {
            return false;
        }

        job.Touch();
        return true;
    }

    private void TouchJob(string? id) => TryGetAndTouch(id, out _);

    private async Task MonitorTimeoutAsync(WorkerJob job, TimeSpan? timeout)
    {
        if (timeout is null || timeout.Value <= TimeSpan.Zero)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        while (!job.Completion.Task.IsCompleted && _jobs.ContainsKey(job.Id))
        {
            await Task.Delay(200);
            // Once every page is upscaled and saved, allow the archive finalization to run
            // without progress events for a generous fixed period instead of the activity
            // timeout (which is scaled by image size and can be shorter than the archive
            // close/flush takes on slow storage).
            TimeSpan effectiveTimeout = job.AllPagesProcessed
                ? PostprocessGracePeriod
                : timeout.Value;
            if (DateTime.UtcNow - job.LastEventUtc > effectiveTimeout)
            {
                _logger.LogWarning(
                    "Upscale worker job {JobId} exceeded the inactivity timeout ({Timeout}); cancelling.",
                    job.Id,
                    effectiveTimeout
                );

                // Mark the timeout before cancelling: the worker may acknowledge the cancel with a
                // "cancelled" done, and the caller must surface the timeout rather than that done.
                job.MarkTimeoutRequested();
                // Fire-and-forget: the cancel must not be awaited on the stdin lock. A worker that
                // stopped reading stdin (the exact wedge this monitor exists to catch) leaves the page
                // producer blocked mid-write while holding that lock, so awaiting the cancel here would
                // block this monitor too and the kill below would never be reached. The grace period
                // below is the worker's whole budget to acknowledge the cancel; the kill then closes
                // the pipe and releases the producer.
                _ = RequestCancelAsync(job.Id);

                Task finished = await Task.WhenAny(
                    job.Completion.Task,
                    Task.Delay(CancelGracePeriod)
                );
                if (
                    finished == job.Completion.Task
                    && job.Completion.Task.Status == TaskStatus.RanToCompletion
                )
                {
                    // The job genuinely finished during the grace period; surface its result
                    // instead of failing it with a timeout.
                    return;
                }

                if (finished != job.Completion.Task)
                {
                    _logger.LogError(
                        "Upscale worker job {JobId} did not cancel in time; killing the worker.",
                        job.Id
                    );
                    await KillWorkerAsync();
                }

                // The worker either ignored the cancel or acknowledged it with a cancelled/error
                // done; surface a timeout with diagnostics in both cases.
                throw new TimeoutException(
                    $"Upscaling timed out after {effectiveTimeout} of inactivity.{BuildStderrSection()}"
                );
            }
        }
    }

    private sealed class WorkerJob
    {
        private long _lastEventTicks;
        private volatile bool _allPagesProcessed;
        private volatile bool _workerExited;
        private volatile bool _timeoutRequested;

        public WorkerJob(
            string id,
            IProgress<UpscaleProgress>? progress,
            Action<UpscaleJobFile>? onPageDone = null,
            bool failOnPageErrors = true
        )
        {
            Id = id;
            Progress = progress;
            OnPageDone = onPageDone;
            FailOnPageErrors = failOnPageErrors;
            Completion = new TaskCompletionSource<UpscaleJobResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _lastEventTicks = DateTime.UtcNow.Ticks;
        }

        public string Id { get; }
        public IProgress<UpscaleProgress>? Progress { get; }
        public Action<UpscaleJobFile>? OnPageDone { get; }

        /// <summary>
        /// Whether a per-file "error" entry should fail the job. The chapter/streaming flow copies a
        /// page the engine could not process through instead (see <see cref="PageStreamClient"/>), so
        /// it must not fail the whole chapter.
        /// </summary>
        public bool FailOnPageErrors { get; }

        public TaskCompletionSource<UpscaleJobResult> Completion { get; }

        public bool AllPagesProcessed => _allPagesProcessed;

        public void MarkAllPagesProcessed() => _allPagesProcessed = true;

        /// <summary>True once the worker process has exited (as opposed to a job-level error).</summary>
        public bool WorkerExited => _workerExited;

        public void MarkWorkerExited() => _workerExited = true;

        /// <summary>
        /// Set by the inactivity monitor just before it cancels a wedged job. The worker may then
        /// acknowledge with a "cancelled" done that faults the job; the timeout, not that done, is
        /// the outcome the caller must surface.
        /// </summary>
        public bool TimeoutRequested => _timeoutRequested;

        public void MarkTimeoutRequested() => _timeoutRequested = true;

        public DateTime LastEventUtc =>
            new(Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);

        public void ReportProgress(int? total, int? current, string? phase) =>
            Progress?.Report(new UpscaleProgress(total, current, phase, null));

        public void TrySetResult(UpscaleJobResult result) => Completion.TrySetResult(result);

        public void Fail(string message) =>
            Completion.TrySetException(new InvalidOperationException(message));

        /// <summary>
        /// Cancels the job. Used when the worker honors a cancel, so the caller sees an
        /// <see cref="OperationCanceledException"/> rather than a failure that would be classified
        /// permanent and drop the spool.
        /// </summary>
        public void Cancel() => Completion.TrySetCanceled();

        /// <summary>
        /// Fails the job with a crash exception so the caller can tell a worker crash (recoverable,
        /// the spool is preserved) from a deterministic job error (terminal).
        /// </summary>
        public void FailCrashed(string message) =>
            Completion.TrySetException(new UpscaleWorkerCrashedException(message));
    }
}
