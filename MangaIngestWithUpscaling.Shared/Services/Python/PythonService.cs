using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.GPU;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

public record EnvironmentState(
    GpuBackend InstalledBackend,
    DateTime CreatedAt,
    string PythonVersion,
    string[] InstalledPackages
);

[RegisterScoped]
public class PythonService(ILogger<PythonService> logger, IGpuDetectionService gpuDetectionService) : IPythonService
{
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

    public async Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory, GpuBackend preferredBackend = GpuBackend.Auto)
    {
        // Determine the actual backend to use
        var targetBackend = await DetermineTargetBackend(preferredBackend);
        
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
        bool needsRecreation = await ShouldRecreateEnvironment(environmentStatePath, targetBackend, relPythonPath);
        
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
            logger.LogInformation("Using existing Python environment with {Backend} backend", targetBackend);
        }

        return new PythonEnvironment(relPythonPath, backendSrcDirectory, targetBackend);
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

    private async Task<bool> ShouldRecreateEnvironment(string environmentStatePath, GpuBackend targetBackend, string pythonPath)
    {
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
            var state = JsonSerializer.Deserialize<EnvironmentState>(stateJson);
            
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

    private async Task InstallPythonPackages(string pythonPath, string backendSrcDirectory, GpuBackend targetBackend, string environmentPath)
    {
        // First, upgrade pip and install wheel
        await RunPipCommand(pythonPath, "install -U pip wheel --no-warn-script-location", environmentPath);
        
        // Install base packages
        var basePackages = "chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 " +
                          "psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 " +
                          "sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1";
        
        await RunPipCommand(pythonPath, $"install {basePackages} --no-warn-script-location", environmentPath);
        
        // Install PyTorch with appropriate backend
        string torchCommand = targetBackend switch
        {
            GpuBackend.CUDA => "install torch==2.7.0 torchvision==0.22.0 --index-url https://download.pytorch.org/whl/cu118 --no-warn-script-location",
            GpuBackend.ROCm => "install torch==2.7.0 torchvision==0.22.0 --index-url https://download.pytorch.org/whl/rocm6.3 --no-warn-script-location",
            GpuBackend.CPU => "install torch==2.7.0 torchvision==0.22.0 --index-url https://download.pytorch.org/whl/cpu --no-warn-script-location",
            _ => "install torch==2.7.0 torchvision==0.22.0 --no-warn-script-location"
        };
        
        logger.LogInformation("Installing PyTorch with {Backend} backend", targetBackend);
        await RunPipCommand(pythonPath, torchCommand, environmentPath);
        
        // Install backend source
        await RunPipCommand(pythonPath, $"install \"{backendSrcDirectory}\" --no-warn-script-location", environmentPath);
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
                installedPackages
            );
            
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(environmentStatePath, json);
            
            logger.LogInformation("Saved environment state with {Backend} backend", installedBackend);
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
        using var process = new Process();
        process.StartInfo.FileName = environment.PythonExecutablePath;
        process.StartInfo.Arguments = $"{script} {arguments}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.WorkingDirectory = environment.DesiredWorkindDirectory;

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var _timeout = timeout ?? TimeSpan.FromSeconds(60);
        var lastActivity = DateTime.UtcNow;

        process.Start();

        // Start reading output streams
        var readOutput = ReadStreamAsync(process.StandardOutput, outputBuilder, () => lastActivity = DateTime.UtcNow);
        var readError = ReadStreamAsync(process.StandardError, errorBuilder, () => lastActivity = DateTime.UtcNow);

        try
        {
            while (true)
            {
                cancellationToken?.ThrowIfCancellationRequested();

                // Check timeout every second
                if (DateTime.UtcNow - lastActivity > _timeout)
                {
                    process.Kill(true);
                    throw new TimeoutException(
                        $"Process timed out after {_timeout.TotalSeconds} seconds of inactivity.\n" +
                        $"Partial error output:\n{errorBuilder}\n\nPartial standard output{outputBuilder}");
                }

                if (process.HasExited)
                {
                    await Task.WhenAll(readOutput, readError);
                    break;
                }

                await Task.Delay(1000, cancellationToken ?? CancellationToken.None);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Python process failed with code {process.ExitCode}\n" +
                    $"Error output:\n{errorBuilder}\n\nPartial standard output{outputBuilder}");
            }

            return outputBuilder.ToString();
        }
        catch (Exception)
        {
            process.Kill(true);
            throw;
        }
        finally
        {
            process.Kill(true);
        }
    }

    private async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder builder,
        Action updateActivity)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                builder.AppendLine(line);
                logger.LogDebug("Python Output: {line}", line);
                updateActivity();
            }
        }
        catch (ObjectDisposedException) { } // Handle process disposal
    }
}