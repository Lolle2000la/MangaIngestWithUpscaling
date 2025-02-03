namespace MangaIngestWithUpscaling.Services.Python;

public interface IPythonService
{
    bool IsPythonInstalled();
    string? GetPythonExecutablePath();
    Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory);
    /// <summary>
    /// Runs a python script in the global environment with the given arguments
    /// </summary>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <param name="timout">A timeout that can be used to cancel the operation if there is no activity.</param>
    /// <returns>A <see cref="IAsyncEnumerable{string}"/> that represents the lines as they are coming from the script.</returns>
    public IAsyncEnumerable<string> RunPythonScript(string script, string arguments, CancellationToken? cancellationToken = null, TimeSpan? timout = null);
    /// <summary>
    /// Runs a python script in the given environment with the given arguments
    /// </summary>
    /// <param name="environment">The environment that should be used for execution.</param>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <param name="timout">A timeout that can be used to cancel the operation if there is no activity.</param>
    /// <returns>A <see cref="IAsyncEnumerable{string}"/> that represents the lines as they are coming from the script.</returns>
    IAsyncEnumerable<string> RunPythonScript(PythonEnvironment environment, string script, string arguments, CancellationToken? cancellationToken = null, TimeSpan? timout = null);
}

