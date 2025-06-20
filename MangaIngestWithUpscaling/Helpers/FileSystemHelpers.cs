namespace MangaIngestWithUpscaling.Helpers;

public class FileSystemHelpers
{
    public static void DeleteEmptySubfolders(string startLocation, ILogger logger)
    {
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
