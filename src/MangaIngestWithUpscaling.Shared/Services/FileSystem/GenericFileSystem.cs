namespace MangaIngestWithUpscaling.Shared.Services.FileSystem;

public class GenericFileSystem : IFileSystem
{
    public void ApplyPermissions(string path)
    {
        // Do nothing since this is not supported on generic file systems
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    /// <inheritdoc/>
    public void Move(string sourceFileName, string destFileName)
    {
        File.Move(sourceFileName, destFileName);
    }

    /// <inheritdoc/>
    public void Move(string sourceFileName, string destFileName, bool overwrite)
    {
        File.Move(sourceFileName, destFileName, overwrite);
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }
}
