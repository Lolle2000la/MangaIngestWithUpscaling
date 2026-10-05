using System.Diagnostics;
using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Processes;

/// <summary>
/// Owns the machinery shared by the resident NDJSON subprocess clients (<c>worker.py</c> and
/// <c>detect_server.py</c>): spawning with the common stdio/encoding flags, the ready handshake,
/// the stdout/stderr pumps, a write-locked line sender with bounded teardown, the idle watchdog
/// and a teardown that kills before disposing stdin. Protocol parsing, job bookkeeping and the
/// job orchestration stay in the concrete client.
/// </summary>
public abstract class ResidentNdjsonProcess : IHostedService, IAsyncDisposable
{
    protected readonly SemaphoreSlim _submitLock = new(1, 1);
    protected readonly Lock _stateLock = new();
    protected Process? _process;
    protected StreamWriter? _stdin;
    protected TaskCompletionSource? _readyTcs;
    protected TaskCompletionSource? _cacheReleaseTcs;
    protected bool _shuttingDown;
    protected readonly IOptions<UpscalerConfig> _config;
    protected readonly ILogger _logger;

    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeSpan _readyTimeout;
    private readonly StderrTailBuffer _stderr = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private CancellationTokenSource? _watchdogCts;

    protected ResidentNdjsonProcess(
        IOptions<UpscalerConfig> config,
        ILogger logger,
        IHostApplicationLifetime lifetime,
        TimeSpan readyTimeout
    )
    {
        _config = config;
        _logger = logger;
        _lifetime = lifetime;
        _readyTimeout = readyTimeout;
    }

    // ----- Hooks implemented by the protocol adapters -----

    /// <summary>True while a job/request is registered or in flight.</summary>
    protected abstract bool HasInFlightJob { get; }

    /// <summary>Parses and dispatches one protocol line read from stdout.</summary>
    protected abstract void HandleStdoutLine(string line);

    /// <summary>Runs the client's exit handling for a process whose stdout reader reached EOF.</summary>
    protected abstract Task OnProcessExitedAsync(Process process);

    /// <summary>Stops and tears down the process; <paramref name="force"/> skips in-flight guards.</summary>
    protected abstract Task ShutdownProcessAsync(bool force, CancellationToken cancellationToken);

    /// <summary>The exception used when stdin is not available.</summary>
    protected abstract Exception CreateStdinUnavailableException();

    /// <summary>The exception used when the process fails to start.</summary>
    protected abstract Exception CreateProcessStartFailureException(Exception? inner);

    /// <summary>
    /// Translates a <see cref="Process.Start"/> failure into a client-specific exception, or returns
    /// <c>null</c> to let the original exception propagate.
    /// </summary>
    protected virtual Exception? TranslateProcessStartException(Exception ex) => null;

    /// <summary>
    /// Translates a stdin write failure into a client-specific exception, or returns <c>null</c> to
    /// let the original exception propagate.
    /// </summary>
    protected virtual Exception? TranslateSendException(Exception ex) => null;

    /// <summary>The stderr diagnostics appended to failure messages.</summary>
    protected virtual string BuildStderrSection()
    {
        string stderr = _stderr.GetTail();
        return stderr.Length > 0 ? $"\n\nWorker stderr (tail):\n{stderr}" : "";
    }

    protected virtual string StderrLogTemplate => "[upscale worker] {Line}";
    protected virtual string StderrConsolePrefix => "[upscale worker]";
    protected virtual string StdoutReaderStoppedLog => "Upscale worker stdout reader stopped.";
    protected virtual string StderrReaderStoppedLog => "Upscale worker stderr reader stopped.";
    protected virtual string WatchdogErrorLog => "Upscale worker idle watchdog error.";
    protected virtual string IdleShutdownLog =>
        "Upscale worker idle for {IdleFor}, shutting down to release GPU resources.";

    // ----- Shared helpers -----

    protected void TouchActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Completes the ready handshake (a <c>ready</c> protocol event was received).</summary>
    protected void SignalReady() => _readyTcs?.TrySetResult();

    /// <summary>
    /// Resolves a pending GPU-cache release from a <c>cache_released</c> event: "ok" means the cache
    /// was released; any other status (e.g. "busy") means it was not.
    /// </summary>
    protected void HandleCacheReleased(string? status)
    {
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            _cacheReleaseTcs?.TrySetResult();
        }
        else
        {
            _cacheReleaseTcs?.TrySetCanceled();
        }
    }

    protected void ClearStderr() => _stderr.Clear();

    protected string GetStderrTail() => _stderr.GetTail();

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> with the flags and encodings shared by every resident
    /// NDJSON subprocess. The caller adds the script and its arguments.
    /// </summary>
    protected static ProcessStartInfo CreateStartInfo(
        PythonEnvironment environment,
        Encoding stdinEncoding
    ) =>
        new()
        {
            FileName = environment.PythonExecutablePath,
            WorkingDirectory = environment.DesiredWorkindDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = stdinEncoding,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

    /// <summary>
    /// Applies the environment variables shared by every resident NDJSON subprocess: a fallback
    /// <c>USER</c> and UTF-8 stdio, so a non-ASCII path is not mangled by the locale encoding.
    /// </summary>
    protected static void ApplyPythonEnvironmentVariables(ProcessStartInfo startInfo)
    {
        if (!startInfo.EnvironmentVariables.ContainsKey("USER"))
        {
            startInfo.EnvironmentVariables["USER"] = "mangaingest";
        }

        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
    }

    /// <summary>
    /// Starts the process, publishes it with its stdin and ready signal, and begins the stdout and
    /// stderr pumps. Returns the process id, captured before publishing so a concurrent exit handler
    /// cannot dispose the process out from under it.
    /// </summary>
    protected int StartProcess(ProcessStartInfo startInfo, TaskCompletionSource readyTcs)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception ex)
        {
            Exception? translated = TranslateProcessStartException(ex);
            if (translated is not null)
            {
                throw translated;
            }

            throw;
        }

        if (!started)
        {
            throw CreateProcessStartFailureException(null);
        }

        int processId = process.Id;
        StreamWriter stdin = process.StandardInput;

        // Publish the process and its stdin together so teardown can't capture a mismatched
        // (process, stdin) pair while a new process is being spawned.
        lock (_stateLock)
        {
            _shuttingDown = false;
            _process = process;
            _stdin = stdin;
            _readyTcs = readyTcs;
        }

        _ = ReadStdoutAsync(process);
        _ = ReadStderrAsync(process);
        return processId;
    }

    /// <summary>
    /// Waits for the ready signal, touching the activity clock every five seconds so the idle
    /// watchdog does not tear the process down during a long spawn/warmup. Callers supply their own
    /// timeout/cancellation handling around this.
    /// </summary>
    protected async Task WaitForReadyAsync(
        TaskCompletionSource readyTcs,
        CancellationToken linkedToken
    )
    {
        while (!readyTcs.Task.IsCompleted)
        {
            TouchActivity();
            try
            {
                await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), linkedToken);
            }
            catch (TimeoutException)
            {
                // Still spawning; loop and touch again.
            }
        }

        await readyTcs.Task;
    }

    protected async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        StreamWriter? stdin = _stdin;
        if (stdin is null)
        {
            throw CreateStdinUnavailableException();
        }

        await SendLineAsync(stdin, line, cancellationToken);
    }

    protected async Task SendLineAsync(
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
        {
            Exception? translated = TranslateSendException(ex);
            if (translated is not null)
            {
                throw translated;
            }

            throw;
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>
    /// Disposes a captured stdin under the stdin lock, so a concurrent
    /// <see cref="SendLineAsync(StreamWriter, string, CancellationToken)"/> cannot write to a
    /// disposed writer (which would silently drop the line).
    /// </summary>
    protected async Task DisposeStdinAsync(StreamWriter? stdin)
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

    private static void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Kills the process (if alive) and waits briefly for it to exit before disposing, so a respawn
    /// does not race the dead process's GPU context (a CUDA context can still hold VRAM after a
    /// kill, causing an OOM on the new process's model load).
    /// </summary>
    protected static async Task KillAndDisposeAsync(Process? process)
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

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                TouchActivity();
                HandleStdoutLine(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, StdoutReaderStoppedLog);
        }
        finally
        {
            await OnProcessExitedAsync(process);
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                // Mirror diagnostics to the host stderr when enabled (default in dev), so they are
                // visible regardless of the configured log level.
                if (_config.Value.WorkerLogToStderr)
                {
                    Console.Error.WriteLine($"{StderrConsolePrefix} {line}");
                }

                _logger.LogDebug(StderrLogTemplate, line);
                _stderr.Append(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, StderrReaderStoppedLog);
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
        await ShutdownProcessAsync(force: true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
        await ShutdownProcessAsync(force: true, CancellationToken.None);
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
                _logger.LogDebug(ex, WatchdogErrorLog);
            }
        }
    }

    private async Task EnsureIdleShutdownAsync()
    {
        // Take the submit lock (non-blocking) so the idle check + shutdown are atomic with respect
        // to a fresh job submission: if a job is being submitted or processed we skip teardown, and
        // once we commit to it a new job waits for us to finish.
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
                idle = !HasInFlightJob;
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
                _logger.LogInformation(IdleShutdownLog, idleFor);
                await ShutdownProcessAsync(force: false, CancellationToken.None);
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }
}
