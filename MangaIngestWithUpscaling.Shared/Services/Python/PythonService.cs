using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

public record EnvironmentState(
    GpuBackend InstalledBackend,
    DateTime CreatedAt,
    string PythonVersion,
    string[] InstalledPackages,
    int EnvironmentVersion
);

[RegisterScoped]
public class PythonService(ILogger<PythonService> logger, IGpuDetectionService gpuDetectionService) : IPythonService
{
    /// <summary>
    ///     Environment version - increment this when Python dependencies change to force environment recreation.
    ///     Version History:
    ///     v1: Initial implementation with torch==2.7.0, torchvision==0.22.0, and base packages
    ///     v2: Updated to torch==2.7.1, torchvision==0.22.1, unified installation approach
    ///     v3: Added Intel XPU support with PyTorch XPU backend from Intel's repository
    ///     v4: Added packaging==25.0 as an explicit dependency
    ///     v5: Added CUDA 12.8 support with cu128 PyTorch wheel index
    ///     v6: Updated to torch==2.8.0, torchvision==0.23.0 for latest PyTorch with proper CUDA 12.8 support
    ///     v7: Did a partial rollback for the standard cuda version for compatibility reasons.
    ///     When updating dependencies:
    ///     1. Update the package versions in InstallPythonPackages method
    ///     2. Increment this ENVIRONMENT_VERSION constant
    ///     3. Add a comment above describing the changes
    /// </summary>
    private const int ENVIRONMENT_VERSION = 7;

    public static PythonEnvironment? Environment { get; set; }

    public string? GetPythonExecutablePath()
    {
        string executableExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

        if (PathHelpers.ExistsOnPath($"python3.12{executableExtension}"))
        {
            return PathHelpers.GetFullPath($"python3.12{executableExtension}");
        }
        else
        {
            logger.LogCritical("Python 3.12 must be installed on the system in order to use upscaling!");
            return null;
        }
    }

    public bool IsPythonInstalled()
    {
        string executableExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        return PathHelpers.ExistsOnPath($"python{executableExtension}") ||
               PathHelpers.ExistsOnPath($"python3{executableExtension}");
    }

    public async Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory,
        GpuBackend preferredBackend = GpuBackend.Auto, bool forceAcceptExisting = false)
    {
        // Determine the actual backend to use
        var targetBackend = forceAcceptExisting ? GpuBackend.CPU : await DetermineTargetBackend(preferredBackend);

        // create a virtual environment in a writable but permanent location
        var environmentPath = Path.GetFullPath(desiredDirectory);
        var environmentStatePath = Path.Combine(environmentPath, "environment_state.json");

        var relPythonPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) switch
        {
            true => Path.Combine(environmentPath, "Scripts", "python3.12.exe"),
            false => Path.Combine(environmentPath, "bin", "python3.12")
        };

        var relPythonBin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) switch
        {
            true => "python3.12.exe",
            false => "python3.12"
        };

        string assemblyDir = AppContext.BaseDirectory;
        string backendSrcDirectory = Path.Combine(assemblyDir, "backend", "src");

        // Check if environment needs to be created or recreated
        bool needsRecreation =
            await ShouldRecreateEnvironment(environmentStatePath, targetBackend, relPythonPath, forceAcceptExisting);

        GpuBackend actualBackend = targetBackend;

        if (needsRecreation)
        {
            logger.LogInformation("Creating/recreating Python environment with {Backend} backend", targetBackend);

            // Remove existing environment if it exists
            if (Directory.Exists(environmentPath))
            {
                Directory.Delete(environmentPath, true);
            }

            await CreateVirtualEnvironment(relPythonBin, environmentPath);
            await InstallPythonPackages(relPythonPath, backendSrcDirectory, targetBackend, environmentPath);
            await SaveEnvironmentState(environmentStatePath, targetBackend, relPythonPath);
        }
        else
        {
            if (forceAcceptExisting)
            {
                logger.LogInformation("Force accepting existing Python environment (backend detection bypassed)");
                // When forcing acceptance, use the preferred backend or fall back to CPU as the safest default
                actualBackend = preferredBackend != GpuBackend.Auto ? preferredBackend : GpuBackend.CPU;
            }
            else
            {
                logger.LogInformation("Using existing Python environment with {Backend} backend", targetBackend);
            }
        }

        return new PythonEnvironment(relPythonPath, backendSrcDirectory, actualBackend);
    }

    public Task<string> RunPythonScript(string script, string arguments, CancellationToken? cancellationToken = null,
        TimeSpan? timout = null)
    {
        if (Environment == null)
        {
            throw new InvalidOperationException("Python environment is not initialized.");
        }

        return RunPythonScript(Environment, script, arguments, cancellationToken, timout);
    }

    public async Task<string> RunPythonScript(
        PythonEnvironment environment,
        string script,
        string arguments,
        CancellationToken? cancellationToken = null,
        TimeSpan? timeout = null)
    {
        var sb = new StringBuilder();
        await RunPythonScriptStreaming(environment, script, arguments, line =>
        {
            sb.AppendLine(line);
            return Task.CompletedTask;
        }, cancellationToken, timeout);
        return sb.ToString();
    }

    public Task RunPythonScriptStreaming(string script, string arguments, Func<string, Task> onStdout,
        CancellationToken? cancellationToken = null, TimeSpan? timeout = null)
    {
        if (Environment == null)
        {
            throw new InvalidOperationException(
                "Python environment is not prepared. Call PreparePythonEnvironment first.");
        }

        return RunPythonScriptStreaming(Environment, script, arguments, onStdout, cancellationToken, timeout);
    }

    public async Task RunPythonScriptStreaming(PythonEnvironment environment, string script, string arguments,
        Func<string, Task> onStdout, CancellationToken? cancellationToken = null, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);

        var startInfo = new ProcessStartInfo
        {
            FileName = environment.PythonExecutablePath,
            Arguments = $"\"{script}\" {arguments}",
            WorkingDirectory = environment.DesiredWorkindDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errorBuilder = new StringBuilder();
        DateTime lastActivity = DateTime.UtcNow;

        void updateActivity() => lastActivity = DateTime.UtcNow;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start python process");
        }

        DataReceivedEventHandler? outputHandler = null;
        DataReceivedEventHandler? errorHandler = null;

        try
        {
            outputHandler = async (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                updateActivity();
                try { await onStdout(e.Data); }
                catch
                {
                    /* swallow to avoid crashing reader */
                }
            };
            errorHandler = (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                updateActivity();
                lock (errorBuilder)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Monitor for timeout based on activity
            while (!process.HasExited)
            {
                await Task.Delay(200, cts.Token);
                if (timeout.HasValue && DateTime.UtcNow - lastActivity > timeout.Value)
                {
                    try { process.Kill(true); }
                    catch { }

                    throw new TimeoutException($"Python process timed out after {timeout.Value} of no output.");
                }
            }

            // Ensure all output drained
            await Task.Delay(50, cts.Token);

            if (process.ExitCode != 0)
            {
                string err;
                lock (errorBuilder)
                {
                    err = errorBuilder.ToString();
                }

                throw new InvalidOperationException($"Python process exited with code {process.ExitCode}: {err}");
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CancelOutputRead();
                }
            }
            catch { }

            try
            {
                if (!process.HasExited)
                {
                    process.CancelErrorRead();
                }
            }
            catch { }

            if (outputHandler is not null)
            {
                try { process.OutputDataReceived -= outputHandler; }
                catch { }
            }

            if (errorHandler is not null)
            {
                try { process.ErrorDataReceived -= errorHandler; }
                catch { }
            }
        }
    }

    private async Task<GpuBackend> DetermineTargetBackend(GpuBackend preferredBackend)
    {
        if (preferredBackend != GpuBackend.Auto)
        {
            logger.LogInformation("Using manually configured backend: {Backend}", preferredBackend);
            return preferredBackend;
        }

        // Auto-detect the best available backend using OpenGL
        logger.LogInformation("Auto-detecting GPU backend using OpenGL...");

        try
        {
            var detectedBackend = await gpuDetectionService.DetectOptimalBackendAsync();
            logger.LogInformation("GPU detection completed, selected backend: {Backend}", detectedBackend);
            return detectedBackend;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error during GPU detection, falling back to CPU");
            return GpuBackend.CPU;
        }
    }

    private async Task<bool> ShouldRecreateEnvironment(string environmentStatePath, GpuBackend targetBackend,
        string pythonPath, bool forceAcceptExisting = false)
    {
        // If force accept is enabled and Python executable exists, accept the environment as-is
        if (forceAcceptExisting && File.Exists(pythonPath))
        {
            logger.LogInformation("Force accepting existing Python environment at {PythonPath}", pythonPath);
            return false;
        }

        // If Python executable doesn't exist, we need to create the environment
        if (!File.Exists(pythonPath))
        {
            return true;
        }

        // If state file doesn't exist, we need to recreate to track the backend
        if (!File.Exists(environmentStatePath))
        {
            return true;
        }

        try
        {
            var stateJson = await File.ReadAllTextAsync(environmentStatePath);
            EnvironmentState? state =
                JsonSerializer.Deserialize(stateJson, PythonServiceJsonContext.Default.EnvironmentState);

            if (state == null)
            {
                return true;
            }

            // If the backend has changed, we need to recreate
            if (state.InstalledBackend != targetBackend)
            {
                logger.LogInformation("Backend changed from {OldBackend} to {NewBackend}, recreating environment",
                    state.InstalledBackend, targetBackend);
                return true;
            }

            // If the environment version has changed, we need to recreate
            if (state.EnvironmentVersion != ENVIRONMENT_VERSION)
            {
                logger.LogInformation(
                    "Environment version changed from {OldVersion} to {NewVersion}, recreating environment",
                    state.EnvironmentVersion, ENVIRONMENT_VERSION);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading environment state, recreating environment");
            return true;
        }
    }

    private async Task CreateVirtualEnvironment(string pythonBin, string environmentPath)
    {
        using var process = new Process();
        process.StartInfo.FileName = pythonBin;
        process.StartInfo.Arguments = $"-m venv {Path.GetFullPath(environmentPath)}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.WorkingDirectory = Directory.GetParent(environmentPath)!.FullName;

        process.Start();

        while (!process.HasExited && !process.StandardOutput.EndOfStream)
        {
            logger.LogInformation(await process.StandardOutput.ReadLineAsync() ?? "<No output>");
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create virtual environment:\n\n {await process.StandardError.ReadToEndAsync()}");
        }
    }

    private async Task InstallPythonPackages(string pythonPath, string backendSrcDirectory, GpuBackend targetBackend,
        string environmentPath)
    {
        // First, upgrade pip and install wheel
        await RunPipCommand(pythonPath, "install -U pip wheel --no-warn-script-location", environmentPath);

        // Install PyTorch and all other packages in a single command with appropriate backend
        string packagesCommand = targetBackend switch
        {
            GpuBackend.CUDA =>
                "install torch==2.7.1 torchvision==0.22.1 --extra-index-url https://download.pytorch.org/whl/cu118 " +
                "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location",
            GpuBackend.CUDA_12_8 =>
                "install torch==2.8.0 torchvision==0.23.0 --extra-index-url https://download.pytorch.org/whl/cu128 " +
                "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location",
            GpuBackend.ROCm =>
                "install torch==2.8.0 torchvision==0.23.0 --extra-index-url https://download.pytorch.org/whl/rocm6.4 " +
                "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location",
            GpuBackend.XPU =>
                "install torch==2.8.0 torchvision==0.23.0 --extra-index-url https://download.pytorch.org/whl/xpu " +
                "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location",
            GpuBackend.CPU =>
                "install torch==2.8.0 torchvision==0.23.0 --extra-index-url https://download.pytorch.org/whl/cpu " +
                "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location",
            _ => "install torch==2.8.0 torchvision==0.23.0 " +
                 "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                 "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                 "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0 --no-warn-script-location"
        };

        logger.LogInformation("Installing PyTorch and dependencies with {Backend} backend", targetBackend);
        await RunPipCommand(pythonPath, packagesCommand, environmentPath);

        // Install backend source
        await RunPipCommand(pythonPath, $"install \"{backendSrcDirectory}\" --no-warn-script-location",
            environmentPath);
    }

    private async Task RunPipCommand(string pythonPath, string pipArgs, string environmentPath)
    {
        string moduleInstallCommand = $"{pythonPath} -m pip {pipArgs}";

        using var process = new Process();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.StartInfo.FileName = "cmd";
            process.StartInfo.Arguments = $"/c {moduleInstallCommand}";
        }
        else
        {
            process.StartInfo.FileName = "sh";
            process.StartInfo.Arguments = $"-c \"{moduleInstallCommand}\"";
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.WorkingDirectory = Directory.GetParent(environmentPath)!.FullName;

        process.Start();
        while (!process.HasExited && !process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (!string.IsNullOrEmpty(line))
            {
                logger.LogDebug("Pip: {Line}", line);
            }
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var errorOutput = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"Failed to run pip command '{pipArgs}':\n\n {errorOutput}");
        }
    }

    private async Task SaveEnvironmentState(string environmentStatePath, GpuBackend installedBackend, string pythonPath)
    {
        try
        {
            // Get installed packages list
            var installedPackages = await GetInstalledPackages(pythonPath);

            // Get Python version
            var pythonVersion = await GetPythonVersion(pythonPath);

            var state = new EnvironmentState(
                installedBackend,
                DateTime.UtcNow,
                pythonVersion,
                installedPackages,
                ENVIRONMENT_VERSION
            );

            string json = JsonSerializer.Serialize(state, PythonServiceJsonContext.Default.EnvironmentState);
            await File.WriteAllTextAsync(environmentStatePath, json);

            logger.LogInformation("Saved environment state with {Backend} backend, version {Version}", installedBackend,
                ENVIRONMENT_VERSION);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save environment state");
        }
    }

    private async Task<string[]> GetInstalledPackages(string pythonPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = pythonPath;
            process.StartInfo.Arguments = "-m pip list --format=freeze";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get installed packages list");
        }

        return Array.Empty<string>();
    }

    private async Task<string> GetPythonVersion(string pythonPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = pythonPath;
            process.StartInfo.Arguments = "--version";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Python version");
        }

        return "Unknown";
    }
}