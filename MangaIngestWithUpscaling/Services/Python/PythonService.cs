
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MangaIngestWithUpscaling.Services.Python;

public class PythonService(ILogger<PythonService> logger) : IPythonService
{
    public static PythonEnvironment? Environment { get; set; }

    public string? GetPythonExecutablePath()
    {
        string executableExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

        if (PathHelpers.ExistsOnPath($"python{executableExtension}"))
        {
            return PathHelpers.GetFullPath($"python{executableExtension}");
        }
        else if (PathHelpers.ExistsOnPath($"python3{executableExtension}"))
        {
            return PathHelpers.GetFullPath($"python3{executableExtension}");
        }
        else
        {
            return null;
        }
    }

    public bool IsPythonInstalled()
    {
        string executableExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        return PathHelpers.ExistsOnPath($"python{executableExtension}") || PathHelpers.ExistsOnPath($"python3{executableExtension}");
    }

    public async Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory)
    {
        // create a virtual environment in a writable but permanent location
        var environmentPath = Path.GetFullPath(desiredDirectory);
        var relPythonPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) switch
        {
            true => Path.Combine(environmentPath, "Scripts", "python.exe"),
            false => Path.Combine(environmentPath, "bin", "python")
        };

        string assemblyDir = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName;
        string backendSrcDirectory = Path.Combine(assemblyDir, "backend", "src");
        if (!Directory.Exists(environmentPath))
        {
            // run the command to create the virtual environment
            using (var process = new Process())
            {
                process.StartInfo.FileName = "python";
                process.StartInfo.Arguments = $"-m venv {Path.GetFullPath(environmentPath)}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                //process.StartInfo.WorkingDirectory = Directory.GetParent(environmentPath)!.FullName;

                process.Start();

                while (process.HasExited && !process.StandardOutput.EndOfStream)
                {
                    logger.LogInformation(await process.StandardOutput.ReadLineAsync() ?? "<No output>");
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create virtual environment:\n\n {await process.StandardError.ReadToEndAsync()}");
                }
            }



            // install the required modules
            string moduleInstallCommand = $@"{relPythonPath} -m pip install -U pip wheel --no-warn-script-location && {relPythonPath} -m pip install torch==2.5.1 torchvision --index-url https://download.pytorch.org/whl/cu124 --no-warn-script-location && {relPythonPath} -m pip install ""{backendSrcDirectory}"" --no-warn-script-location";

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
                    throw new InvalidOperationException($"Failed to install required modules:\n\n {await process.StandardError.ReadToEndAsync()}");
                }
            }
        }

        return new PythonEnvironment(relPythonPath, backendSrcDirectory);
    }

    public IAsyncEnumerable<string> RunPythonScript(string script, string arguments, CancellationToken? cancellationToken = null, TimeSpan? timout = null)
    {
        if (Environment == null)
        {
            throw new InvalidOperationException("Python environment is not initialized.");
        }

        return RunPythonScript(Environment, script, arguments, cancellationToken, timout);
    }

    public async IAsyncEnumerable<string> RunPythonScript(PythonEnvironment environment, string script, string arguments,
        [EnumeratorCancellation] CancellationToken? cancellationToken = null, TimeSpan? timout = null)
    {
        CancellationToken token = cancellationToken ?? CancellationToken.None;
        using (var process = new Process())
        {
            process.StartInfo.FileName = environment.PythonExecutablePath;
            process.StartInfo.Arguments = $"{script} {arguments}";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.StartInfo.WorkingDirectory = environment.DesiredWorkindDirectory;

            process.Start();

            var _timeout = timout ?? TimeSpan.FromSeconds(60); // Adjust timeout as needed
            var lastOutputTime = DateTime.UtcNow;
            var cts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);

            // Read stderr and update last activity time
            var stderrTask = ReadAndUpdateActivity(process.StandardError, () => lastOutputTime = DateTime.UtcNow, linkedCts.Token);

            try
            {
                while (true)
                {
                    // Check if timeout has been exceeded
                    if (DateTime.UtcNow - lastOutputTime >= _timeout)
                    {
                        process.Kill();
                        throw new TimeoutException($"No output received from the process within {_timeout.TotalSeconds} seconds.");
                    }

                    var readOutTask = process.StandardOutput.ReadLineAsync();
                    var delayTask = Task.Delay(_timeout, linkedCts.Token);
                    var completedTask = await Task.WhenAny(readOutTask, delayTask, stderrTask);

                    if (completedTask == delayTask)
                    {
                        process.Kill();
                        throw new TimeoutException($"No output received from the process within {_timeout.TotalSeconds} seconds.");
                    }
                    else if (completedTask == readOutTask)
                    {
                        var line = await readOutTask;
                        if (line == null) break; // End of stream

                        lastOutputTime = DateTime.UtcNow;
                        yield return line;
                    }
                    else
                    {
                        // stderrTask completed (stream closed), check process exit
                        await process.WaitForExitAsync(linkedCts.Token);
                        break;
                    }
                }

                // Verify exit code
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Script failed: {await process.StandardError.ReadToEndAsync(linkedCts.Token)}");
                }
            }
            finally
            {
                cts.Cancel();
                await stderrTask;
            }
        }
    }

    private async Task ReadAndUpdateActivity(StreamReader reader, Action updateActivity, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                updateActivity();
            }
        }
        catch (OperationCanceledException) { }
    }
}
