namespace MangaIngestWithUpscaling.Services.RepairServices;

/// <summary>
/// Context for repair operations that manages temporary files and directories.
/// </summary>
public class RepairContext : IDisposable
{
    public string WorkDirectory { get; set; } = string.Empty;
    public string UpscaledDirectory { get; set; } = string.Empty;
    public string MissingPagesCbz { get; set; } = string.Empty;
    public string UpscaledMissingCbz { get; set; } = string.Empty;
    public bool HasMissingPages { get; set; }

    public void Dispose()
    {
        if (Directory.Exists(WorkDirectory))
        {
            try
            {
                DeleteWorkDirectory();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    ///     Deletes the temporary work directory. Exposed as a virtual method so tests can substitute
    ///     a slow delete and verify that callers do not hold a lock across it.
    /// </summary>
    protected virtual void DeleteWorkDirectory() => Directory.Delete(WorkDirectory, true);
}
