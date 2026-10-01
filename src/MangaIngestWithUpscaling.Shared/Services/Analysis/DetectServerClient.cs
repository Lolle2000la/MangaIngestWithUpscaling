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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<UpscalerConfig> _config;
    private readonly ILogger<DetectServerClient> _logger;

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

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    private readonly StderrTailBuffer _stderr = new();

    public DetectServerClient(
        IServiceScopeFactory scopeFactory,
        IOptions<UpscalerConfig> config,
        ILogger<DetectServerClient> logger
    )
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
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

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cacheReleaseTcs = tcs;
        }

        try
        {
            await SendLineAsync(
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WatchdogLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        ShutdownServerAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await ShutdownServerAsync(CancellationToken.None);

    public async Task ShutdownServerAsync(CancellationToken cancellationToken)
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
            if (stdin is not null)
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
            _logger.LogDebug(ex, "Error while shutting down the detection server.");
        }

        await CleanupAsync(process);
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(serverScript);
        startInfo.ArgumentList.Add("--checkpoint");
        startInfo.ArgumentList.Add(checkpoint);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(config);
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
        _logger.LogInformation("Resident detection server started (pid {Pid}).", process.Id);
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

    private static void TryKillAndDispose(Process? process)
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
            }
        }
        catch { }

        TryDispose(process);
    }

    private Task CleanupAsync(Process? process)
    {
        if (process is null)
        {
            return Task.CompletedTask;
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
                stdin = null;
            }
        }

        TryDispose(stdin);
        TryKillAndDispose(process);

        _logger.LogInformation("Resident detection server process stopped.");
        return Task.CompletedTask;
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
            OnServerExited(process);
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
        return stderr.Length > 0 ? $"\n\nDetection server stderr (tail):\n{stderr}" : "";
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
                case DetectServerCacheReleasedEvent:
                    _cacheReleaseTcs?.TrySetResult();
                    break;
                case DetectServerPongEvent:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse detection server event: {Line}", line);
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

    private void OnServerExited(Process process)
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

        TryDispose(stdin);

        foreach (DetectJob job in _jobs.Values.ToArray())
        {
            job.Completion.TrySetException(
                new DetectServerUnavailableException(
                    $"The detection server exited unexpectedly (exit code {detail}).{stderrSection}"
                )
            );
        }

        readyTcs?.TrySetException(
            new DetectServerUnavailableException(
                "The detection server exited before becoming ready."
            )
        );

        TryKillAndDispose(process);
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
