namespace MangaIngestWithUpscaling.Services.Python;

public interface IPythonService
{
    bool IsPythonInstalled();
    Task<PythonEnvironment> PreparePythonEnvironment(string desiredDirectory);
    /// <summary>
    /// Runs a python script in the given environment with the given arguments
    /// </summary>
    /// <param name="environment">The environment that should be used for execution.</param>
    /// <param name="script">The actual script to run</param>
    /// <param name="arguments">The arguments to the script</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>A <see cref="IAsyncEnumerable{string}"/> that represents the lines as they are coming from the script.</returns>
    IAsyncEnumerable<string> RunPythonScript(PythonEnvironment environment, string script, string arguments, CancellationToken? cancellationToken = null);
}

