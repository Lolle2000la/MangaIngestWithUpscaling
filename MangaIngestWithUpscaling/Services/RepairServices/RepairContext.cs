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
                Directory.Delete(WorkDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}