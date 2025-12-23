namespace MangaIngestWithUpscaling.Shared.Services.FileSystem;

public interface IFileSystem
{
    /// <summary>
    /// Moves a file from one location to another, applying the necessary permissions.
    /// </summary>
    /// <param name="sourceFileName">The file to be moved.</param>
    /// <param name="destFileName">The file moved.</param>
    void Move(string sourceFileName, string destFileName);

    /// <summary>
    /// Creates a directory at the specified path with the necessary permissions.
    /// </summary>
    /// <param name="path">The path to the directory(s) to create.</param>
    void CreateDirectory(string path);

    /// <summary>
    /// Applies the necessary permissions to the specified path to either the parent directory or the configured parent.
    /// </summary>
    /// <param name="path">Path to the file for which to apply the permissions.</param>
    void ApplyPermissions(string path);

    /// <summary>
    /// Determines whether the specified file exists.
    /// </summary>
    /// <param name="path">The file to check.</param>
    /// <returns>true if the file exists; otherwise, false.</returns>
    bool FileExists(string path);
}
