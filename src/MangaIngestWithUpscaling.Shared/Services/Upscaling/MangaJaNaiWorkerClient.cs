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
    private DateTime _lastActivityUtc = DateTime.UtcNow;

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
            _lastActivityUtc = DateTime.UtcNow;

            await EnsureWorkerAsync(cancellationToken);

            var job = new WorkerJob(request.Id, progress);
            if (!_jobs.TryAdd(request.Id, job))
            {
                throw new InvalidOperationException(
                    $"A job with id '{request.Id}' is already in flight."
                );
            }

            _currentJobId = request.Id;

            try
            {
                await SendLineAsync(BuildJobLine(request), cancellationToken);

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
                _lastActivityUtc = DateTime.UtcNow;
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
            await readyTcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await KillWorkerAsync();
            throw new InvalidOperationException(
                "Timed out waiting for the upscale worker to become ready."
            );
        }

        _lastActivityUtc = DateTime.UtcNow;
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

        TimeSpan idleFor = DateTime.UtcNow - _lastActivityUtc;
        if (idle && idleFor >= _config.Value.WorkerIdleTimeout)
        {
            _logger.LogInformation(
                "Upscale worker idle for {IdleFor}, shutting down to release GPU resources.",
                idleFor
            );
            await ShutdownWorkerAsync(CancellationToken.None);
        }
    }

    // -- NDJSON framing / event handling -------------------------------------- //

    internal static string BuildJobLine(UpscaleJobRequest request)
    {
        var job = new Dictionary<string, object?>
        {
            ["type"] = "job",
            ["id"] = request.Id,
            ["input"] = new Dictionary<string, object?> { ["path"] = request.InputPath },
            ["output"] = new Dictionary<string, object?>
            {
                ["folder"] = request.OutputFolder,
                ["filename"] = request.OutputFilename,
                ["format"] = ToFormatString(request.Format),
                ["overwrite"] = request.Overwrite,
            },
            ["options"] = new Dictionary<string, object?> { ["scale"] = (int)request.Scale },
        };
        return JsonSerializer.Serialize(job);
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
                _lastActivityUtc = DateTime.UtcNow;
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
                _logger.LogDebug("[upscale worker] {Line}", line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upscale worker stderr reader stopped.");
        }
    }

    private void HandleEvent(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl))
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "ready":
                    _readyTcs?.TrySetResult();
                    break;
                case "progress":
                    DispatchProgress(root);
                    break;
                case "done":
                    DispatchDone(root);
                    break;
                case "error":
                    DispatchError(root);
                    break;
                case "rejected":
                    DispatchRejected(root);
                    break;
                case "accepted":
                case "started":
                    // Reset the per-job inactivity clock when the worker acknowledges/starts a job,
                    // so the model-loading gap before the first progress event is not counted as idle.
                    TouchJob(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse upscale worker event: {Line}", line);
        }
    }

    private void DispatchProgress(JsonElement root)
    {
        if (!TryGetJob(root, out WorkerJob? job))
        {
            return;
        }

        job!.Touch();
        job.ReportProgress(GetInt(root, "archive_total"), GetInt(root, "completed"));
    }

    private void DispatchDone(JsonElement root)
    {
        if (!TryGetJob(root, out WorkerJob? job))
        {
            return;
        }

        job!.Touch();

        string status = root.TryGetProperty("status", out JsonElement s)
            ? s.GetString() ?? "ok"
            : "ok";
        double elapsed =
            root.TryGetProperty("elapsed_seconds", out JsonElement e)
            && e.TryGetDouble(out double ev)
                ? ev
                : 0;

        var files = new List<UpscaleJobFile>();
        if (
            root.TryGetProperty("files", out JsonElement filesEl)
            && filesEl.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement file in filesEl.EnumerateArray())
            {
                files.Add(
                    new UpscaleJobFile(
                        file.TryGetProperty("input", out JsonElement i) ? i.GetString() ?? "" : "",
                        file.TryGetProperty("output", out JsonElement o) ? o.GetString() ?? "" : "",
                        file.TryGetProperty("status", out JsonElement fs)
                            ? fs.GetString() ?? ""
                            : ""
                    )
                );
            }
        }

        job.TrySetResult(new UpscaleJobResult(job.Id, status, files, elapsed));
    }

    private void DispatchError(JsonElement root)
    {
        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
        string message = root.TryGetProperty("message", out JsonElement m)
            ? m.GetString() ?? ""
            : "";

        if (id is not null && _jobs.TryGetValue(id, out WorkerJob? job))
        {
            job.Touch();
            job.TrySetException(new InvalidOperationException($"Upscale worker error: {message}"));
            return;
        }

        _logger.LogWarning("Upscale worker reported an error: {Message}", message);
    }

    private void DispatchRejected(JsonElement root)
    {
        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
        string reason = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() ?? "" : "";

        if (id is not null && _jobs.TryGetValue(id, out WorkerJob? job))
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
        foreach (WorkerJob job in _jobs.Values.ToArray())
        {
            job.TrySetException(
                new InvalidOperationException(
                    $"Upscale worker process exited unexpectedly (exit code {detail})."
                )
            );
        }

        _readyTcs?.TrySetException(
            new InvalidOperationException("Upscale worker exited before becoming ready.")
        );

        lock (_stateLock)
        {
            if (_process == process)
            {
                _process = null;
                _readyTcs = null;
                _stdin = null;
            }
        }

        try
        {
            process.Dispose();
        }
        catch { }
    }

    private static int? GetInt(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement el) && el.TryGetInt32(out int v)
            ? v
            : null;
    }

    private bool TryGetJob(JsonElement root, out WorkerJob? job)
    {
        job = null;
        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
        return id is not null && _jobs.TryGetValue(id, out job);
    }

    private void TouchJob(JsonElement root)
    {
        if (TryGetJob(root, out WorkerJob? job))
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

        while (!job.Completion.Task.IsCompleted)
        {
            await Task.Delay(200);
            if (DateTime.UtcNow - job.LastEventUtc > timeout.Value)
            {
                _logger.LogWarning(
                    "Upscale worker job {JobId} exceeded the inactivity timeout ({Timeout}); cancelling.",
                    job.Id,
                    timeout.Value
                );

                await RequestCancelAsync(job.Id);

                Task finished = await Task.WhenAny(
                    job.Completion.Task,
                    Task.Delay(CancelGracePeriod)
                );
                if (finished != job.Completion.Task)
                {
                    _logger.LogError(
                        "Upscale worker job {JobId} did not cancel in time; killing the worker.",
                        job.Id
                    );
                    await KillWorkerAsync();
                }

                throw new TimeoutException(
                    $"Upscaling timed out after {timeout.Value} of inactivity."
                );
            }
        }
    }

    // -- job state -------------------------------------------------------------- //

    private sealed class WorkerJob
    {
        private long _lastEventTicks;

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

        public DateTime LastEventUtc =>
            new(Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);

        public void ReportProgress(int? total, int? current) =>
            Progress?.Report(new UpscaleProgress(total, current, null, null));

        public void TrySetResult(UpscaleJobResult result) => Completion.TrySetResult(result);

        public void TrySetException(Exception exception) => Completion.TrySetException(exception);
    }
}
