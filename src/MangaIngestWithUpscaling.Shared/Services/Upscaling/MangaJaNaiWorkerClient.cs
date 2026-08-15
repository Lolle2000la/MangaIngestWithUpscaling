using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Owns a long-running <c>worker.py</c> subprocess and routes upscale jobs to it over NDJSON.
/// The process is spawned lazily on the first job, kept alive so models/GPU stay warm across jobs,
/// and torn down by <see cref="WatchdogLoopAsync"/> once it has been idle for
/// <see cref="UpscalerConfig.WorkerIdleTimeout"/>.
/// </summary>
public class MangaJaNaiWorkerClient : IMangaJaNaiWorkerClient, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(10);

    // After every page is upscaled and saved, the only remaining work is finalizing the
    // output archive, which produces no progress events. Give it a fixed grace period
    // instead of the pixel-scaled inactivity timeout.
    private static readonly TimeSpan PostprocessGracePeriod = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<UpscalerConfig> _config;
    private readonly ILogger<MangaJaNaiWorkerClient> _logger;

    private readonly SemaphoreSlim _submitLock = new(1, 1);
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly ConcurrentDictionary<string, WorkerJob> _jobs = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _readyTcs;
    private string? _currentJobId;
    private bool _shuttingDown;

    // Stored as ticks so the stdout reader, watchdog, and job loop can update/read it
    // without torn reads on a DateTime struct.
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    private readonly Lock _stderrLock = new();
    private readonly StringBuilder _stderrBuffer = new();

    public MangaJaNaiWorkerClient(
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> config,
        ILogger<MangaJaNaiWorkerClient> logger
    )
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    // -- public API ---------------------------------------------------------- //

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
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

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
                catch (InvalidOperationException)
                    when (_stdin is null && !cancellationToken.IsCancellationRequested)
                {
                    // The worker crashed between the ready check and submission; respawn once
                    // with a fresh job and retry.
                    _logger.LogWarning(
                        "Upscale worker crashed during submission; respawning and retrying once."
                    );
                    _jobs.TryRemove(request.Id, out _);
                    job = new WorkerJob(request.Id, progress);
                    _jobs.TryAdd(request.Id, job);
                    await EnsureWorkerAsync(cancellationToken);
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

                Task monitor = MonitorTimeoutAsync(job, timeout);

                Task completed = await Task.WhenAny(
                    job.Completion.Task,
                    monitor,
                    cancelSignal.Task
                );

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

                if (completed == monitor)
                {
                    await monitor; // throws TimeoutException after escalating cancel/kill
                }

                return await job.Completion.Task;
            }
            finally
            {
                _jobs.TryRemove(request.Id, out _);
                _currentJobId = null;
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    public async Task ShutdownWorkerAsync(CancellationToken cancellationToken)
    {
        Process? process;
        StreamWriter? stdin;
        lock (_stateLock)
        {
            process = _process;
            stdin = _stdin;
            _shuttingDown = true;
            if (process is null || process.HasExited)
            {
                _process = null;
                return;
            }
        }

        try
        {
            // Write to the captured stdin, not the shared _stdin field, so a concurrent
            // spawn can't make us shut down the *new* worker by mistake.
            if (stdin is not null)
            {
                await _stdinLock.WaitAsync();
                try
                {
                    await stdin.WriteLineAsync(
                        JsonSerializer
                            .Serialize(new Dictionary<string, object?> { ["type"] = "shutdown" })
                            .AsMemory()
                    );
                    await stdin.FlushAsync();
                }
                finally
                {
                    _stdinLock.Release();
                }
            }

            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while shutting down the upscale worker.");
        }

        await CleanupAsync(process);
    }

    // -- IHostedService / disposal ------------------------------------------- //

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WatchdogLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        ShutdownWorkerAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await ShutdownWorkerAsync(CancellationToken.None);

    // -- worker lifecycle ---------------------------------------------------- //

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
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
        lock (_stderrLock)
        {
            _stderrBuffer.Clear();
        }

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

        var startInfo = new ProcessStartInfo
        {
            FileName = environment.PythonExecutablePath,
            WorkingDirectory = environment.DesiredWorkindDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("worker.py");
        startInfo.ArgumentList.Add("--settings");
        startInfo.ArgumentList.Add(settingsPath);
        startInfo.ArgumentList.Add("--queue-capacity");
        startInfo.ArgumentList.Add(_config.Value.WorkerQueueCapacity.ToString());
        if (_config.Value.WorkerWarmup)
        {
            startInfo.ArgumentList.Add("--warmup");
        }

        if (!startInfo.EnvironmentVariables.ContainsKey("USER"))
        {
            startInfo.EnvironmentVariables["USER"] = "mangaingest";
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the upscale worker process.");
        }

        StreamWriter stdin = process.StandardInput;

        // Publish the process and its stdin together so ShutdownWorkerAsync can't capture
        // a mismatched (process, stdin) pair while a new worker is being spawned.
        lock (_stateLock)
        {
            _shuttingDown = false;
            _process = process;
            _stdin = stdin;
            _readyTcs = readyTcs;
        }

        _ = ReadStdoutAsync(process);
        _ = ReadStderrAsync(process);

        using var timeoutCts = new CancellationTokenSource(ReadyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token
        );
        try
        {
            // Keep the idle watchdog from tearing the worker down during a long
            // spawn/warmup: touch the activity clock periodically until ready completes.
            while (!readyTcs.Task.IsCompleted)
            {
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                try
                {
                    await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
                }
                catch (TimeoutException)
                {
                    // Still spawning; loop and touch again.
                }
            }

            await readyTcs.Task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await KillWorkerAsync();
            throw new InvalidOperationException(
                "Timed out waiting for the upscale worker to become ready."
            );
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled while we were waiting for ready; tear down the spawned
            // worker so it doesn't linger until the next job or shutdown.
            await KillWorkerAsync();
            throw;
        }

        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        _logger.LogInformation(
            "Upscale worker started (pid {Pid}) using settings {SettingsPath}.",
            process.Id,
            settingsPath
        );
    }

    private async Task KillWorkerAsync()
    {
        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _shuttingDown = true;
        }

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
        lock (_stateLock)
        {
            if (_process == process)
            {
                _process = null;
                _readyTcs = null;
                stdin = _stdin;
                _stdin = null;
            }
            else
            {
                // A newer worker has already replaced this one; leave its stdin alone.
                stdin = null;
            }
        }

        try
        {
            stdin?.Dispose();
        }
        catch { }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch { }

        try
        {
            process.Dispose();
        }
        catch { }

        _logger.LogInformation("Upscale worker process stopped.");
    }

    // -- idle teardown ------------------------------------------------------- //

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(poll, cancellationToken);
                await EnsureIdleShutdownAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Upscale worker idle watchdog error.");
            }
        }
    }

    private async Task EnsureIdleShutdownAsync()
    {
        // Take the submit lock (non-blocking) so the idle check + shutdown are atomic with
        // respect to a fresh job submission: if a job is being submitted or processed we skip
        // teardown, and once we commit to it a new job waits for us to finish.
        if (!await _submitLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            Process? process;
            bool idle;
            lock (_stateLock)
            {
                process = _process;
                idle = _currentJobId is null && _jobs.IsEmpty;
                if (process is null || process.HasExited)
                {
                    return;
                }
            }

            TimeSpan idleFor =
                DateTime.UtcNow
                - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            if (idle && idleFor >= _config.Value.WorkerIdleTimeout)
            {
                _logger.LogInformation(
                    "Upscale worker idle for {IdleFor}, shutting down to release GPU resources.",
                    idleFor
                );
                await ShutdownWorkerAsync(CancellationToken.None);
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    // -- NDJSON framing / event handling -------------------------------------- //

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
            },
            Options = new WorkerJobOptions { Scale = (int)request.Scale },
        };
        return JsonSerializer.Serialize(job, WorkerJson.Options);
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

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        await _stdinLock.WaitAsync(cancellationToken);
        try
        {
            if (_stdin is null)
            {
                throw new InvalidOperationException("Upscale worker stdin is not available.");
            }

            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task RequestCancelAsync(string jobId)
    {
        try
        {
            await SendLineAsync(
                JsonSerializer.Serialize(
                    new Dictionary<string, object?> { ["type"] = "cancel", ["id"] = jobId }
                ),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send cancel for job {JobId}.", jobId);
        }
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                HandleEvent(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upscale worker stdout reader stopped.");
        }
        finally
        {
            OnWorkerExited(process);
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                // Mirror worker diagnostics to the host stderr when enabled (default in dev),
                // so they are visible regardless of the configured log level.
                if (_config.Value.WorkerLogToStderr)
                {
                    Console.Error.WriteLine($"[upscale worker] {line}");
                }

                _logger.LogDebug("[upscale worker] {Line}", line);
                AppendStderr(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upscale worker stderr reader stopped.");
        }
    }

    private void AppendStderr(string line)
    {
        const int maxLength = 8192;
        lock (_stderrLock)
        {
            if (_stderrBuffer.Length + line.Length + 1 > maxLength)
            {
                // Drop the oldest half so the tail (most recent diagnostics) is preserved.
                _stderrBuffer.Remove(0, _stderrBuffer.Length / 2);
            }

            _stderrBuffer.AppendLine(line);
        }
    }

    private string GetStderrTail()
    {
        lock (_stderrLock)
        {
            return _stderrBuffer.ToString();
        }
    }

    private string BuildStderrSection()
    {
        string stderr = GetStderrTail();
        return stderr.Length > 0 ? $"\n\nWorker stderr (tail):\n{stderr}" : "";
    }

    private void HandleEvent(string line)
    {
        try
        {
            WorkerEvent? evt = JsonSerializer.Deserialize<WorkerEvent>(line, WorkerJson.Options);
            switch (evt)
            {
                case WorkerReadyEvent:
                    _readyTcs?.TrySetResult();
                    break;
                case WorkerProgressEvent progress:
                    DispatchProgress(progress);
                    break;
                case WorkerDoneEvent done:
                    DispatchDone(done);
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse upscale worker event: {Line}", line);
        }
    }

    private void DispatchProgress(WorkerProgressEvent progress)
    {
        if (!TryGetJob(progress.Id, out WorkerJob? job))
        {
            return;
        }

        job!.Touch();

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
        if (!TryGetJob(done.Id, out WorkerJob? job))
        {
            return;
        }

        job!.Touch();

        string status = done.Status ?? "ok";
        var files = (done.Files ?? [])
            .Select(f => new UpscaleJobFile(f.Input ?? "", f.Output ?? "", f.Status ?? ""))
            .ToList();

        if (status != "ok")
        {
            job.TrySetException(
                new InvalidOperationException($"Upscale worker reported status '{status}'.")
            );
            return;
        }

        string[] failed = files.Where(f => f.Status == "error").Select(f => f.Input).ToArray();
        if (failed.Length > 0)
        {
            job.TrySetException(
                new InvalidOperationException(
                    $"Upscale worker failed to process {failed.Length} file(s): {string.Join(", ", failed)}"
                )
            );
            return;
        }

        job.TrySetResult(new UpscaleJobResult(job.Id, status, files, done.ElapsedSeconds));
    }

    private void DispatchError(WorkerErrorEvent error)
    {
        string message = error.Message ?? "";

        if (error.Id is not null && _jobs.TryGetValue(error.Id, out WorkerJob? job))
        {
            job.Touch();
            job.TrySetException(new InvalidOperationException($"Upscale worker error: {message}"));
            return;
        }

        _logger.LogWarning("Upscale worker reported an error: {Message}", message);
    }

    private void DispatchRejected(WorkerRejectedEvent rejected)
    {
        string reason = rejected.Reason ?? "";

        if (rejected.Id is not null && _jobs.TryGetValue(rejected.Id, out WorkerJob? job))
        {
            job.TrySetException(
                new InvalidOperationException($"Upscale worker rejected the job: {reason}")
            );
        }
    }

    private void OnWorkerExited(Process process)
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
        }

        try
        {
            stdin?.Dispose();
        }
        catch { }

        foreach (WorkerJob job in _jobs.Values.ToArray())
        {
            job.TrySetException(
                new InvalidOperationException(
                    $"Upscale worker process exited unexpectedly (exit code {detail}).{stderrSection}"
                )
            );
        }

        readyTcs?.TrySetException(
            new InvalidOperationException("Upscale worker exited before becoming ready.")
        );

        try
        {
            process.Dispose();
        }
        catch { }
    }

    private bool TryGetJob(string? id, out WorkerJob? job)
    {
        job = null;
        return id is not null && _jobs.TryGetValue(id, out job);
    }

    private void TouchJob(string? id)
    {
        if (TryGetJob(id, out WorkerJob? job))
        {
            job!.Touch();
        }
    }

    // -- timeout escalation ---------------------------------------------------- //

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

                await RequestCancelAsync(job.Id);

                Task finished = await Task.WhenAny(
                    job.Completion.Task,
                    Task.Delay(CancelGracePeriod)
                );
                if (finished == job.Completion.Task)
                {
                    // The job finished during the grace period; let its result (success or
                    // failure) surface normally instead of failing it with a timeout.
                    return;
                }

                _logger.LogError(
                    "Upscale worker job {JobId} did not cancel in time; killing the worker.",
                    job.Id
                );
                await KillWorkerAsync();

                throw new TimeoutException(
                    $"Upscaling timed out after {effectiveTimeout} of inactivity.{BuildStderrSection()}"
                );
            }
        }
    }

    // -- job state -------------------------------------------------------------- //

    private sealed class WorkerJob
    {
        private long _lastEventTicks;
        private volatile bool _allPagesProcessed;

        public WorkerJob(string id, IProgress<UpscaleProgress>? progress)
        {
            Id = id;
            Progress = progress;
            Completion = new TaskCompletionSource<UpscaleJobResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _lastEventTicks = DateTime.UtcNow.Ticks;
        }

        public string Id { get; }
        public IProgress<UpscaleProgress>? Progress { get; }
        public TaskCompletionSource<UpscaleJobResult> Completion { get; }

        public bool AllPagesProcessed => _allPagesProcessed;

        public void MarkAllPagesProcessed() => _allPagesProcessed = true;

        public DateTime LastEventUtc =>
            new(Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);

        public void ReportProgress(int? total, int? current, string? phase) =>
            Progress?.Report(new UpscaleProgress(total, current, phase, null));

        public void TrySetResult(UpscaleJobResult result) => Completion.TrySetResult(result);

        public void TrySetException(Exception exception) => Completion.TrySetException(exception);
    }
}
