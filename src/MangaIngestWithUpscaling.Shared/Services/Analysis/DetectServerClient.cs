using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// Owns a long-running <c>detect_server.py</c> subprocess and routes detection requests to it over
/// NDJSON. Spawned lazily on first use, kept alive so the model stays resident across pages, and
/// torn down by <see cref="WatchdogLoopAsync"/> once idle for
/// <see cref="UpscalerConfig.WorkerIdleTimeout"/>.
/// </summary>
public sealed class DetectServerClient : IDetectServerClient, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How long a failed startup is remembered before the server is attempted again. Retrying per
    ///     request would burn a full <see cref="ReadyTimeout" /> on every page of a chapter on a host
    ///     where the model never loads in time — before each request falls back to the CLI anyway.
    /// </summary>
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Encoding for the detection server's stdin. Must not emit a UTF-8 BOM: the server does
    /// <c>json.loads(line)</c>, which rejects a leading BOM, so a BOM would make every request fail
    /// to parse and stall for the full request timeout.
    /// </summary>
    public static Encoding StdinEncoding { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<UpscalerConfig> _config;
    private readonly ILogger<DetectServerClient> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly SemaphoreSlim _submitLock = new(1, 1);
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly ConcurrentDictionary<string, DetectJob> _jobs = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _readyTcs;
    private TaskCompletionSource? _cacheReleaseTcs;
    private string? _currentJobId;
    private bool _shuttingDown;
    private DateTime _unavailableUntilUtc;
    private CancellationTokenSource? _watchdogCts;

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    private readonly StderrTailBuffer _stderr = new();

    // Non-JSON lines the server prints to stdout (e.g. a model-load traceback before it becomes
    // ready). They are not protocol events, but they carry the failure cause, so keep a tail.
    private readonly StderrTailBuffer _stdoutNoise = new();

    public DetectServerClient(
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> config,
        ILogger<DetectServerClient> logger,
        IHostApplicationLifetime lifetime
    )
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _lifetime = lifetime;
    }

    private void TouchActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

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
                string line = JsonSerializer.Serialize(
                    new DetectServerRequest { Id = id, Path = imagePath },
                    DetectServerJsonContext.Default.DetectServerRequest
                );
                await SendLineAsync(line, cancellationToken);

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                    _ = RequestCancelAsync(id)
                );

                TimeSpan timeout = _config.Value.DetectServerRequestTimeout;
                try
                {
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
            if (stdin is not null)
            {
                await SendLineAsync(
                    stdin,
                    JsonSerializer.Serialize(
                        new DetectServerCommand("release_cache"),
                        DetectServerJsonContext.Default.DetectServerCommand
                    ),
                    cancellationToken
                );
            }

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Link the host's startup token with ApplicationStopping: StartAsync's token only fires when
        // startup is aborted, so without the link the watchdog would keep polling after shutdown.
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.ApplicationStopping
        );
        _ = WatchdogLoopAsync(_watchdogCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _watchdogCts?.Cancel();
        await ShutdownServerAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
        await ShutdownServerAsync(CancellationToken.None);
    }

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

        _stderr.Clear();
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

        var startInfo = new ProcessStartInfo
        {
            FileName = environment.PythonExecutablePath,
            WorkingDirectory = environment.DesiredWorkindDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // A BOM would be written before the first JSON line and break the server's json.loads.
            StandardInputEncoding = StdinEncoding,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
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

        if (!startInfo.EnvironmentVariables.ContainsKey("USER"))
        {
            startInfo.EnvironmentVariables["USER"] = "mangaingest";
        }

        // Pin Python's stdio to UTF-8. The C# side writes BOM-less UTF-8, but Python reads stdin with
        // the locale encoding unless told otherwise, so a non-ASCII page path would be mangled on
        // Windows/ANSI or a bare C locale.
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (!process.Start())
            {
                throw new DetectServerUnavailableException(
                    "Failed to start the resident detection server process."
                );
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new DetectServerUnavailableException(
                "Failed to start the resident detection server process.",
                ex
            );
        }

        // Capture the pid before publishing the process: the stdout reader can observe EOF and dispose
        // the process (OnServerExited) at any point, after which reading process.Id would throw an
        // InvalidOperationException that the caller does not treat as a fallback-eligible failure.
        int processId = process.Id;

        StreamWriter stdin = process.StandardInput;
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
            while (!readyTcs.Task.IsCompleted)
            {
                TouchActivity();
                try
                {
                    await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
                }
                catch (TimeoutException)
                {
                    // Still loading the model; loop and touch again.
                }
            }

            await readyTcs.Task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
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

    private static void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Disposes a captured stdin under the stdin lock, so a concurrent <see cref="SendLineAsync(string, CancellationToken)"/>
    /// cannot write to a disposed writer (which would silently drop the line).
    /// </summary>
    private async Task DisposeStdinAsync(StreamWriter? stdin)
    {
        if (stdin is null)
        {
            return;
        }

        await _stdinLock.WaitAsync();
        try
        {
            TryDispose(stdin);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>
    /// Kills the process (if alive) and waits briefly for it to exit before disposing, so a respawn
    /// does not race the dead process's GPU context (a CUDA context can still hold VRAM after a kill,
    /// causing an OOM on the new server's model load).
    /// </summary>
    private static async Task KillAndDisposeAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(exitCts.Token);
                }
                catch (OperationCanceledException) { }
            }
        }
        catch { }

        TryDispose(process);
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
                // Fault in-flight jobs here too. OnServerExited early-returns when _process no longer
                // matches (a forced shutdown nulls it first), so without this a DetectAsync would
                // block on its job.Completion for the full request timeout while holding _submitLock.
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

        await DisposeStdinAsync(stdin);
        // Unblock a pending GPU-cache release so its caller does not wait the full timeout.
        cacheRelease?.TrySetCanceled();
        // Unblock a startup wait: EnsureServerAsync awaits this TCS, and OnServerExited will not fault
        // it once _process has been cleared, so a shutdown during startup would otherwise stall for
        // the full ReadyTimeout.
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

        await KillAndDisposeAsync(process);

        _logger.LogInformation("Resident detection server process stopped.");
    }

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
                _logger.LogDebug(ex, "Detection server idle watchdog error.");
            }
        }
    }

    private async Task EnsureIdleShutdownAsync()
    {
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
                    "Detection server idle for {IdleFor}, shutting down to release GPU resources.",
                    idleFor
                );
                await ShutdownServerAsync(CancellationToken.None);
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        StreamWriter? stdin = _stdin;
        if (stdin is null)
        {
            throw new DetectServerUnavailableException(
                "The detection server stdin is not available."
            );
        }

        await SendLineAsync(stdin, line, cancellationToken);
    }

    private async Task SendLineAsync(
        StreamWriter stdin,
        string line,
        CancellationToken cancellationToken
    )
    {
        await _stdinLock.WaitAsync(cancellationToken);
        try
        {
            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken);
            await stdin.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or ObjectDisposedException
                        or Win32Exception
                        or InvalidOperationException
            )
        {
            // A broken pipe or a dead writer means the resident server is unusable; surface it as
            // "unavailable" so the caller falls back to the per-image CLI instead of failing.
            throw new DetectServerUnavailableException(
                "The detection server's stdin is not writable.",
                ex
            );
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

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                TouchActivity();
                HandleEvent(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detection server stdout reader stopped.");
        }
        finally
        {
            await OnServerExited(process);
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                if (_config.Value.WorkerLogToStderr)
                {
                    Console.Error.WriteLine($"[detect server] {line}");
                }

                _logger.LogDebug("[detect server] {Line}", line);
                _stderr.Append(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detection server stderr reader stopped.");
        }
    }

    private string BuildStderrSection()
    {
        string stderr = _stderr.GetTail();
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
                    _readyTcs?.TrySetResult();
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
                    if (
                        string.Equals(
                            cacheReleased.Status,
                            "ok",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        _cacheReleaseTcs?.TrySetResult();
                    }
                    else
                    {
                        _cacheReleaseTcs?.TrySetCanceled();
                    }
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

    private async Task OnServerExited(Process process)
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
            // Snapshot the jobs while the exiting process is still current, so a concurrent
            // DetectAsync that respawns the server and registers a job is not faulted by this stale
            // exit handler.
            jobs = _jobs.Values.ToArray();
        }

        await DisposeStdinAsync(stdin);
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

        await KillAndDisposeAsync(process);
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

    private sealed class StderrTailBuffer
    {
        private const int MaxLength = 8192;
        private readonly Lock _lock = new();
        private readonly StringBuilder _buffer = new();

        public void Append(string line)
        {
            lock (_lock)
            {
                if (_buffer.Length + line.Length + 1 > MaxLength)
                {
                    _buffer.Remove(0, _buffer.Length / 2);
                }

                _buffer.AppendLine(line);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _buffer.Clear();
            }
        }

        public string GetTail()
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }
    }
}
