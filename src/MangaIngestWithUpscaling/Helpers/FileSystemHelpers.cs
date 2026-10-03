namespace MangaIngestWithUpscaling.Helpers;

public class FileSystemHelpers
{
    public static void DeleteEmptySubfolders(string startLocation, ILogger logger)
    {
        if (!Directory.Exists(startLocation))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(startLocation))
        {
            DeleteEmptySubfolders(directory, logger);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    var directoryInfo = new DirectoryInfo(directory);
                    directoryInfo.Attributes = FileAttributes.Normal;
                    directoryInfo.Delete();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deleting directory {directory}", directory);
            }
        }
    }

    /// <summary>
    ///     How long a temp replacement is left alone before a sweep may reclaim it. Long enough that a
    ///     replace still in progress — including one on another replica sharing this library — is never
    ///     touched.
    /// </summary>
    public static readonly TimeSpan DefaultReplaceTempRetention = TimeSpan.FromHours(24);

    /// <summary>
    ///     A temp path next to <paramref name="destinationPath" />, for building a replacement that is
    ///     then moved onto the destination. On one filesystem that move is a rename, so a concurrent
    ///     reader sees either the old file or the new one. A temp under the system temp directory
    ///     usually sits on another mount, where the move becomes a copy that rewrites the destination in
    ///     place — a worker streaming pages from that chapter would read a half-written archive.
    /// </summary>
    public static string TempSiblingPathFor(string destinationPath, string label) =>
        Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{label}.{Guid.NewGuid():N}.tmp"
        );

    /// <summary>
    ///     Reclaims temp replacements an interrupted run left behind: a hard kill between creating the
    ///     temp and moving it skips every delete-on-failure path. Only files older than
    ///     <paramref name="retention" /> are removed so an in-progress replace keeps its temp.
    /// </summary>
    public static void DeleteStaleTempSiblings(
        string directory,
        ILogger logger,
        TimeSpan? retention = null
    )
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - (retention ?? DefaultReplaceTempRetention);
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!IsTempReplacementName(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        logger.LogInformation("Removed stale temp file {Path}.", path);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to remove stale temp file {Path}.", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to sweep stale temp files in {Directory}.", directory);
        }
    }

    /// <summary>
    ///     True for the names <see cref="TempSiblingPathFor" /> and the page-assembly path produce, so a
    ///     sweep cannot touch unrelated files that happen to sit in a library directory.
    /// </summary>
    private static bool IsTempReplacementName(string name) =>
        name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal);

    public static bool DeleteIfEmpty(string path, ILogger logger)
    {
        if (!Directory.Exists(path))
            return false;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                var directoryInfo = new DirectoryInfo(path);
                directoryInfo.Attributes = FileAttributes.Normal;
                directoryInfo.Delete();
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting directory {path}", path);
        }
        return false;
    }
}
