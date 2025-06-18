using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

[RegisterScoped]
public class PythonService(ILogger<PythonService> logger) : IPythonService
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

    public async Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory)
    {
        // create a virtual environment in a writable but permanent location
        var environmentPath = Path.GetFullPath(desiredDirectory);
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

        string assemblyDir = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName;
        string backendSrcDirectory = Path.Combine(assemblyDir, "backend", "src");
        if (!File.Exists(relPythonPath))
        {
            // run the command to create the virtual environment
            using (var process = new Process())
            {
                process.StartInfo.FileName = relPythonBin;
                process.StartInfo.Arguments = $"-m venv {Path.GetFullPath(environmentPath)}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                process.StartInfo.WorkingDirectory = Directory.GetParent(environmentPath)!.FullName;

                process.Start();

                while (process.HasExited && !process.StandardOutput.EndOfStream)
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


            // install the required modules
            string moduleInstallCommand =
                $@"{relPythonPath} -m pip install -U pip wheel --no-warn-script-location && {relPythonPath} -m pip install ""{backendSrcDirectory}"" --no-warn-script-location";

            using (var process = new Process())
            {
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
                    logger.LogInformation(await process.StandardOutput.ReadLineAsync() ?? "<No output>");
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to install required modules:\n\n {await process.StandardError.ReadToEndAsync()}");
                }
            }
        }

        return new PythonEnvironment(relPythonPath, backendSrcDirectory);
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