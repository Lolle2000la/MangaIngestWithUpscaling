using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Processes;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// Owns a long-running <c>detect_server.py</c> subprocess and routes detection requests to it over
/// NDJSON. Spawned lazily on first use, kept alive so the model stays resident across pages, and
/// torn down by the inherited idle watchdog once idle for
/// <see cref="UpscalerConfig.WorkerIdleTimeout"/>.
/// </summary>
public sealed class DetectServerClient : ResidentNdjsonProcess, IDetectServerClient
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How long a failed startup is remembered before the server is attempted again. Retrying per
    ///     request would burn a full <see cref="ReadyTimeout" /> on every page of a chapter on a host
    ///     where the model never loads in time — before each request falls back to the CLI anyway.
    /// </summary>
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a runtime failure (a request timeout or a post-ready crash) is remembered. Shorter
    ///     than <see cref="UnavailableCooldown" /> because, unlike a model that never loads, a respawn may
    ///     well succeed; but a detector that fails on every page must not pay a fresh spawn (plus up to
    ///     the ready and request timeouts) per page.
    /// </summary>
    private static readonly TimeSpan RuntimeFailureCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Encoding for the detection server's stdin. Must not emit a UTF-8 BOM: the server does
    /// <c>json.loads(line)</c>, which rejects a leading BOM, so a BOM would make every request fail
    /// to parse and stall for the full request timeout.
    /// </summary>
    public static Encoding StdinEncoding { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, DetectJob> _jobs = new();

    private string? _currentJobId;
    private DateTime _unavailableUntilUtc;

    // Non-JSON lines the server prints to stdout (e.g. a model-load traceback before it becomes
    // ready). They are not protocol events, but they carry the failure cause, so keep a tail.
    private readonly StderrTailBuffer _stdoutNoise = new();

    public DetectServerClient(
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> config,
        ILogger<DetectServerClient> logger,
        IHostApplicationLifetime lifetime
    )
        : base(config, logger, lifetime, ReadyTimeout)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<SplitDetectionResult> DetectAsync(
        string imagePath,
        CancellationToken cancellationToken
    )
    {
        await _submitLock.WaitAsync(cancellationToken);
        try
        {
            TouchActivity();
            await EnsureServerAsync(cancellationToken);

            string id = Guid.NewGuid().ToString("N");
            var job = new DetectJob(id, imagePath);
            if (!_jobs.TryAdd(id, job))
            {
                throw new DetectServerUnavailableException(
                    "Could not register a detection request with the resident server."
                );
            }

            _currentJobId = id;

            try
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                    _ = RequestCancelAsync(id)
                );

                TimeSpan timeout = _config.Value.DetectServerRequestTimeout;
                try
                {
                    // The send is inside this try: a cancellation that lands while acquiring the stdin
                    // lock (or writing) must still reach the busy-detector kill below instead of
                    // propagating past it and leaving the detector working on a request nobody wants.
                    string line = JsonSerializer.Serialize(
                        new DetectServerRequest { Id = id, Path = imagePath },
                        DetectServerJsonContext.Default.DetectServerRequest
                    );
                    await SendLineAsync(line, cancellationToken);

                    return timeout > TimeSpan.Zero
                        ? await job.Completion.Task.WaitAsync(timeout, cancellationToken)
                        : await job.Completion.Task.WaitAsync(cancellationToken);
                }
                catch (TimeoutException)
                {
                    // A wedged detector (CUDA hang, blocked I/O) must not hang the task forever, and
                    // holding _submitLock would also block the idle watchdog. Kill the process and
                    // surface "unavailable" so the caller falls back to the per-image CLI.
                    _logger.LogError(
                        "Detection request {Id} timed out after {Timeout}; killing the detection server.",
                        id,
                        timeout
                    );
                    await KillServerAsync();
                    ArmRuntimeFailureCooldown();
                    throw new DetectServerUnavailableException(
                        $"The detection server timed out after {timeout}."
                    );
                }
                catch (OperationCanceledException)
                {
                    // The caller gave up while the detector was still busy. It answers requests one at a
                    // time, so leaving it running would park the next detection behind work nobody wants
                    // (until that request's own timeout) and lock out the idle watchdog. Kill it unless it
                    // already answered, and let the next request start from a responsive server.
                    if (!job.Completion.Task.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Detection request {Id} was cancelled while the server was busy; killing the detection server.",
                            id
                        );
                        await KillServerAsync();
                    }

                    throw;
                }
            }
            finally
            {
                _jobs.TryRemove(id, out _);
                _currentJobId = null;
                TouchActivity();
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    public async Task<bool> ReleaseGpuCacheAsync(CancellationToken cancellationToken)
    {
        Process? process;
        StreamWriter? stdin;
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
                _logger.LogDebug(
                    "Not releasing detection server GPU cache: a detection is in flight."
                );
                return false;
            }

            if (_cacheReleaseTcs is not null)
            {
                _logger.LogDebug(
                    "Not releasing detection server GPU cache: a release is already in flight."
                );
                return false;
            }

            // Send to the captured stdin, not the shared field, so a respawn cannot make us address
            // the new process (which would mis-acknowledge the release).
            stdin = _stdin;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cacheReleaseTcs = tcs;
        }

        try
        {
            if (stdin is null)
            {
                // No server to ask; do not wait out the full timeout for a release that was never sent.
                return false;
            }

            await SendLineAsync(
                stdin,
                JsonSerializer.Serialize(
                    new DetectServerCommand("release_cache"),
                    DetectServerJsonContext.Default.DetectServerCommand
                ),
                cancellationToken
            );

            // A healthy idle server answers immediately; the timeout only guards a wedged process.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to release the detection server GPU cache.");
            return false;
        }
        finally
        {
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
        ShutdownServerAsync(cancellationToken);

    protected override bool HasInFlightJob => _currentJobId is not null || !_jobs.IsEmpty;

    protected override void HandleStdoutLine(string line) => HandleEvent(line);

    protected override Exception CreateStdinUnavailableException() =>
        new DetectServerUnavailableException("The detection server stdin is not available.");

    protected override Exception? TranslateProcessStartException(Exception ex) =>
        ex is Win32Exception or InvalidOperationException
            ? new DetectServerUnavailableException(
                "Failed to start the resident detection server process.",
                ex
            )
            : null;

    protected override Exception CreateProcessStartFailureException(Exception? inner) =>
        inner is null
            ? new DetectServerUnavailableException(
                "Failed to start the resident detection server process."
            )
            : new DetectServerUnavailableException(
                "Failed to start the resident detection server process.",
                inner
            );

    protected override Exception? TranslateSendException(Exception ex) =>
        ex is IOException or ObjectDisposedException or Win32Exception or InvalidOperationException
            ? new DetectServerUnavailableException(
                "The detection server's stdin is not writable.",
                ex
            )
            : null;

    protected override string BuildStderrSection()
    {
        string stderr = GetStderrTail();
        string stdoutNoise = _stdoutNoise.GetTail();
        var builder = new StringBuilder();
        if (stderr.Length > 0)
        {
            builder.Append("\n\nDetection server stderr (tail):\n").Append(stderr);
        }

        if (stdoutNoise.Length > 0)
        {
            builder
                .Append("\n\nDetection server stdout (non-protocol, tail):\n")
                .Append(stdoutNoise);
        }

        return builder.ToString();
    }

    protected override string StderrLogTemplate => "[detect server] {Line}";
    protected override string StderrConsolePrefix => "[detect server]";
    protected override string StdoutReaderStoppedLog => "Detection server stdout reader stopped.";
    protected override string StderrReaderStoppedLog => "Detection server stderr reader stopped.";
    protected override string WatchdogErrorLog => "Detection server idle watchdog error.";
    protected override string IdleShutdownLog =>
        "Detection server idle for {IdleFor}, shutting down to release GPU resources.";

    public async Task ShutdownServerAsync(CancellationToken cancellationToken)
    {
        Process? process;
        StreamWriter? stdin;
        lock (_stateLock)
        {
            process = _process;
            stdin = _stdin;
            _shuttingDown = true;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited && stdin is not null)
            {
                await SendLineAsync(
                    stdin,
                    JsonSerializer.Serialize(
                        new DetectServerCommand("shutdown"),
                        DetectServerJsonContext.Default.DetectServerCommand
                    ),
                    CancellationToken.None
                );
            }

            if (!process.HasExited)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                grace.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while shutting down the detection server.");
        }

        // Always dispose, even when the process had already exited; CleanupAsync clears _process
        // and _stdin when they still point at this process.
        await CleanupAsync(process);
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        DateTime unavailableUntil;
        lock (_stateLock)
        {
            unavailableUntil = _unavailableUntilUtc;
        }

        if (unavailableUntil > DateTime.UtcNow)
        {
            // A failed startup is remembered, so the caller's CLI fallback is taken immediately instead
            // of paying another full ready wait on a host where the model does not load in time.
            throw new DetectServerUnavailableException(
                $"The resident detection server is not retried until {unavailableUntil:u} after a failed startup."
            );
        }

        try
        {
            await StartServerAsync(cancellationToken);
        }
        catch (DetectServerUnavailableException ex)
        {
            DateTime until = DateTime.UtcNow + UnavailableCooldown;
            lock (_stateLock)
            {
                _unavailableUntilUtc = until;
            }

            // Logged here rather than per request: the cause is the start failure, and every request in
            // the cooldown window would otherwise repeat the whole stderr tail.
            _logger.LogWarning(
                ex,
                "Resident detection server startup failed; not retrying it until {Until:u}.",
                until
            );
            throw;
        }
    }

    /// <summary>
    ///     Remembers a runtime failure (a request timeout or a post-ready crash) so the next request
    ///     takes the cheap CLI path instead of paying another spawn plus timeout. Never shortens an
    ///     existing cooldown.
    /// </summary>
    private void ArmRuntimeFailureCooldown()
    {
        DateTime until = DateTime.UtcNow + RuntimeFailureCooldown;
        lock (_stateLock)
        {
            if (until > _unavailableUntilUtc)
            {
                _unavailableUntilUtc = until;
            }
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
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

        if (existing is not null)
        {
            await CleanupAsync(existing);
        }

        ClearStderr();
        _stdoutNoise.Clear();

        string serverScript = SplitDetectionLayout.ServerScriptPath;
        if (!File.Exists(serverScript))
        {
            throw new DetectServerUnavailableException(
                $"The resident detection server script was not found at '{serverScript}'."
            );
        }

        string checkpoint = SplitDetectionLayout.CheckpointPath;
        string config = SplitDetectionLayout.ConfigPath;
        if (!File.Exists(checkpoint) || !File.Exists(config))
        {
            throw new DetectServerUnavailableException(
                $"The page-break model files were not found at '{checkpoint}' / '{config}'."
            );
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
            throw new DetectServerUnavailableException(
                "Python environment is not initialized. Call PreparePythonEnvironment first."
            );
        }

        // A BOM would be written before the first JSON line and break the server's json.loads.
        ProcessStartInfo startInfo = CreateStartInfo(environment, StdinEncoding);
        startInfo.ArgumentList.Add(serverScript);
        startInfo.ArgumentList.Add("--checkpoint");
        startInfo.ArgumentList.Add(checkpoint);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(config);
        // Exit the server when this process dies, so an abruptly killed host does not leave a warm
        // model (and its GPU memory) resident forever.
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        if (_config.Value.WorkerIdleCacheReleaseTimeout > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("--idle-cache-release");
            startInfo.ArgumentList.Add(
                _config.Value.WorkerIdleCacheReleaseTimeout.TotalSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        }

        ApplyPythonEnvironmentVariables(startInfo);

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The pid is captured before publishing the process: the stdout reader can observe EOF and
        // dispose the process (OnProcessExitedAsync) at any point, after which reading process.Id
        // would throw an InvalidOperationException the caller does not treat as fallback-eligible.
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
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            string stderrSection = BuildStderrSection();
            await KillServerAsync();
            throw new DetectServerUnavailableException(
                $"Timed out waiting for the detection server to become ready.{stderrSection}"
            );
        }
        catch (OperationCanceledException)
        {
            await KillServerAsync();
            throw;
        }

        TouchActivity();
        _logger.LogInformation("Resident detection server started (pid {Pid}).", processId);
    }

    private async Task KillServerAsync()
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
            _logger.LogDebug(ex, "Error killing the detection server.");
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
        TaskCompletionSource? cacheRelease;
        TaskCompletionSource? readyTcs;
        DetectJob[] jobs;
        lock (_stateLock)
        {
            if (_process == process)
            {
                _process = null;
                readyTcs = _readyTcs;
                _readyTcs = null;
                stdin = _stdin;
                _stdin = null;
                cacheRelease = _cacheReleaseTcs;
                _cacheReleaseTcs = null;
                // Fault in-flight jobs here too. OnProcessExitedAsync early-returns when _process no
                // longer matches (a forced shutdown nulls it first), so without this a DetectAsync
                // would block on its job.Completion for the full request timeout while holding
                // _submitLock.
                jobs = _jobs.Values.ToArray();
            }
            else
            {
                stdin = null;
                cacheRelease = null;
                readyTcs = null;
                jobs = [];
            }
        }

        // Unblock a pending GPU-cache release so its caller does not wait the full timeout.
        cacheRelease?.TrySetCanceled();
        // Unblock a startup wait: EnsureServerAsync awaits this TCS, and OnProcessExitedAsync will
        // not fault it once _process has been cleared, so a shutdown during startup would otherwise
        // stall for the full ReadyTimeout.
        readyTcs?.TrySetException(
            new DetectServerUnavailableException(
                "The detection server was shut down before becoming ready."
            )
        );

        foreach (DetectJob job in jobs)
        {
            job.Completion.TrySetException(
                new DetectServerUnavailableException(
                    "The detection server was shut down while the request was in flight."
                )
            );
        }

        // Kill before disposing stdin: a wedged server's writer can hold _stdinLock while blocked on a
        // full pipe, and waiting on it here would stall teardown. Killing closes the pipe and unblocks
        // the writer. MangaJaNaiWorkerClient uses the same order.
        await KillAndDisposeAsync(process);
        await DisposeStdinAsync(stdin);

        _logger.LogInformation("Resident detection server process stopped.");
    }

    private async Task RequestCancelAsync(string jobId)
    {
        try
        {
            await SendLineAsync(
                JsonSerializer.Serialize(
                    new DetectServerCommand("cancel", jobId),
                    DetectServerJsonContext.Default.DetectServerCommand
                ),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send cancel for detection {JobId}.", jobId);
        }
    }

    private void HandleEvent(string line)
    {
        try
        {
            DetectServerEvent? evt = JsonSerializer.Deserialize(
                line,
                DetectServerJsonContext.Default.DetectServerEvent
            );
            switch (evt)
            {
                case DetectServerReadyEvent:
                    SignalReady();
                    break;
                case DetectServerResultEvent result:
                    if (TryGetJob(result.Id, out DetectJob? resultJob))
                    {
                        resultJob.Completion.TrySetResult(
                            result.Result
                                ?? new SplitDetectionResult
                                {
                                    ImagePath = resultJob.ImagePath,
                                    Error = "The detection server returned an empty result.",
                                }
                        );
                    }
                    break;
                case DetectServerErrorEvent error:
                    if (TryGetJob(error.Id, out DetectJob? errorJob))
                    {
                        // Per-image failures are surfaced as a result with Error set (matching the
                        // CLI) rather than an exception, so a bad page does not fail the chapter.
                        errorJob.Completion.TrySetResult(
                            new SplitDetectionResult
                            {
                                ImagePath = errorJob.ImagePath,
                                Error = error.Message ?? "Page-break detection failed.",
                            }
                        );
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Detection server reported an error: {Message}",
                            error.Message
                        );
                    }
                    break;
                case DetectServerCancelledEvent cancelled:
                    if (TryGetJob(cancelled.Id, out DetectJob? cancelledJob))
                    {
                        cancelledJob.Completion.TrySetCanceled();
                    }
                    break;
                case DetectServerCacheReleasedEvent cacheReleased:
                    // "ok" released the cache; "busy" (a detection is running) did not, so the caller
                    // must not be told the release succeeded.
                    HandleCacheReleased(cacheReleased.Status);
                    break;
                case DetectServerPongEvent:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse detection server event: {Line}", line);
            // A model-load failure is printed to stdout as a plain traceback, which is not a protocol
            // event; keep it so the "exited before becoming ready" error is not cause-less.
            _stdoutNoise.Append(line);
        }
    }

    private bool TryGetJob(
        string? id,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DetectJob? job
    )
    {
        job = null;
        return id is not null && _jobs.TryGetValue(id, out job);
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
        DetectJob[] jobs;
        lock (_stateLock)
        {
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
            // A post-ready crash (a deliberate kill sets _shuttingDown) is remembered so a detector that
            // dies on every page does not pay a fresh spawn per page. The timeout path arms its own.
            if (!_shuttingDown)
            {
                DateTime until = DateTime.UtcNow + RuntimeFailureCooldown;
                if (until > _unavailableUntilUtc)
                {
                    _unavailableUntilUtc = until;
                }
            }
            // Snapshot the jobs while the exiting process is still current, so a concurrent
            // DetectAsync that respawns the server and registers a job is not faulted by this stale
            // exit handler.
            jobs = _jobs.Values.ToArray();
        }

        // Unblock a pending GPU-cache release so its caller does not wait the full timeout.
        cacheRelease?.TrySetCanceled();

        foreach (DetectJob job in jobs)
        {
            job.Completion.TrySetException(
                new DetectServerUnavailableException(
                    $"The detection server exited unexpectedly (exit code {detail}).{stderrSection}"
                )
            );
        }

        readyTcs?.TrySetException(
            new DetectServerUnavailableException(
                $"The detection server exited before becoming ready.{stderrSection}"
            )
        );

        // Kill/dispose before disposing stdin, so a writer blocked on the (now broken) pipe cannot
        // hold _stdinLock across teardown.
        await KillAndDisposeAsync(process);
        await DisposeStdinAsync(stdin);
    }

    private sealed class DetectJob
    {
        public DetectJob(string id, string imagePath)
        {
            Id = id;
            ImagePath = imagePath;
            Completion = new TaskCompletionSource<SplitDetectionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        public string Id { get; }
        public string ImagePath { get; }
        public TaskCompletionSource<SplitDetectionResult> Completion { get; }
    }
}
