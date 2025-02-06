using MangaIngestWithUpscaling.Services.ChapterManagement;

namespace MangaIngestWithUpscaling.Services.FileSystem;

public class FileSystemHelpers
{
    public static void DeleteEmpty(string startLocation, ILogger logger)
    {
        foreach (var directory in Directory.GetDirectories(startLocation))
        {
            DeleteEmpty(directory, logger);
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
}
