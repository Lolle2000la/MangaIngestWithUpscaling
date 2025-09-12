using MangaIngestWithUpscaling.Shared.Configuration;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

public interface IPythonService
{
    bool IsPythonInstalled();
    string? GetPythonExecutablePath();

    Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory,
        GpuBackend preferredBackend = GpuBackend.Auto, bool forceAcceptExisting = false);

    /// <summary>
    /// Runs a python script in the global environment with the given arguments
    /// </summary>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <param name="timout">A timeout that can be used to cancel the operation if there is no activity.</param>
    /// <returns>Full standard output captured as a single string.</returns>
    public Task<string> RunPythonScript(string script, string arguments, CancellationToken? cancellationToken = null,
        TimeSpan? timout = null);

    /// <summary>
    /// Runs a python script in the given environment with the given arguments
    /// </summary>
    /// <param name="environment">The environment that should be used for execution.</param>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <param name="timout">A timeout that can be used to cancel the operation if there is no activity.</param>
    /// <returns>Full standard output captured as a single string.</returns>
    Task<string> RunPythonScript(PythonEnvironment environment, string script, string arguments,
        CancellationToken? cancellationToken = null, TimeSpan? timout = null);

    /// <summary>
    /// Runs a python script and streams stdout line-by-line to the provided callback. Honors timeout based on activity.
    /// </summary>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="onStdout">Callback receiving each stdout line as it arrives.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <param name="timeout">A timeout that cancels when there is no stdout activity within the specified duration.</param>
    Task RunPythonScriptStreaming(string script, string arguments, Func<string, Task> onStdout,
        CancellationToken? cancellationToken = null, TimeSpan? timeout = null);

    /// <summary>
    /// Runs a python script in a given environment and streams stdout line-by-line to the provided callback. Honors timeout based on activity.
    /// </summary>
    Task RunPythonScriptStreaming(PythonEnvironment environment, string script, string arguments,
        Func<string, Task> onStdout, CancellationToken? cancellationToken = null, TimeSpan? timeout = null);
}