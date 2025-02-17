using Mono.Unix.Native;

namespace MangaIngestWithUpscaling.Services.FileSystem;

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
}
