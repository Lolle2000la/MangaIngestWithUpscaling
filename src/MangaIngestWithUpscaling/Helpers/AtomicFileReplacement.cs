using MangaIngestWithUpscaling.Shared.Services.FileSystem;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
///     Owns the "build a replacement beside its destination, then atomically move it into place"
///     sequence, including the stale-temp sweep that reclaims a replacement an interrupted run left
///     behind. Keeping the sequence in one place stops the atomicity and cleanup from drifting
///     between call sites.
/// </summary>
/// <remarks>
///     A replacement is built next to the file it replaces and moved onto it, so the swap is a
///     same-directory rename: a concurrent reader sees either the old file or the new one, never a
///     half-written archive. A temp under the system temp directory usually sits on another mount,
///     where the move becomes a copy that rewrites the destination in place.
/// </remarks>
internal sealed class AtomicFileReplacement : IDisposable
{
    private readonly string _destinationPath;
    private readonly ILogger _logger;
    private bool _committed;

    private AtomicFileReplacement(string destinationPath, string tempPath, ILogger logger)
    {
        _destinationPath = destinationPath;
        TempPath = tempPath;
        _logger = logger;
    }

    /// <summary>
    ///     The path to build the replacement at. It sits next to the destination so the commit is a
    ///     same-directory rename.
    /// </summary>
    public string TempPath { get; }

    /// <summary>
    ///     Sweeps stale replacements beside <paramref name="destinationPath" /> and reserves a fresh
    ///     sibling temp named by <see cref="FileSystemHelpers.TempSiblingPathFor" />.
    /// </summary>
    public static AtomicFileReplacement Begin(
        string destinationPath,
        string label,
        ILogger logger
    ) =>
        Prepare(
            destinationPath,
            FileSystemHelpers.TempSiblingPathFor(destinationPath, label),
            logger
        );

    /// <summary>
    ///     Variant for a call site with its own temp name (the page-assembly path). The name must still
    ///     be a dot-prefixed ".tmp" beside the destination so the sweep recognizes it.
    /// </summary>
    public static AtomicFileReplacement BeginWithTempPath(
        string destinationPath,
        string tempPath,
        ILogger logger
    ) => Prepare(destinationPath, tempPath, logger);

    private static AtomicFileReplacement Prepare(
        string destinationPath,
        string tempPath,
        ILogger logger
    )
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (directory is not null)
        {
            // A hard kill between building the temp and moving it skips every delete-on-failure path,
            // so reclaim what an earlier run left before adding another.
            FileSystemHelpers.DeleteStaleTempSiblings(directory, logger);
        }

        return new AtomicFileReplacement(destinationPath, tempPath, logger);
    }

    /// <summary>
    ///     Atomically replaces the destination with the built temp (a same-directory rename), then
    ///     marks the replacement consumed so <see cref="Dispose" /> leaves it alone.
    /// </summary>
    public void Commit()
    {
        File.Move(TempPath, _destinationPath, overwrite: true);
        _committed = true;
    }

    /// <summary>
    ///     Commit variant for call sites that move through <see cref="IFileSystem" />, preserving its
    ///     permission handling.
    /// </summary>
    public void Commit(IFileSystem fileSystem)
    {
        fileSystem.Move(TempPath, _destinationPath, overwrite: true);
        _committed = true;
    }

    /// <summary>
    ///     Removes a replacement that was built but never committed, so a failed run does not leave a
    ///     full-size temp in the library. Best-effort: a cleanup failure must not mask the original
    ///     exception.
    /// </summary>
    public void Dispose()
    {
        if (_committed)
        {
            return;
        }

        try
        {
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the unused replacement {Temp}.", TempPath);
        }
    }
}
