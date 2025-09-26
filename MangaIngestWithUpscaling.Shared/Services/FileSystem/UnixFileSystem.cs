using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mono.Unix.Native;

namespace MangaIngestWithUpscaling.Shared.Services.FileSystem;

public class UnixFileSystem(
    IOptions<UnixPermissionsConfig> permissionsConfig,
    ILogger<UnixFileSystem> logger
) : IFileSystem
{
    public ILogger<UnixFileSystem> Logger { get; } = logger;

    public void ApplyPermissions(string path)
    {
        path = Path.GetFullPath(path);
        string? parentDirectory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Unable to get parent directory for {path}");

        // Retrieve parent's permissions
        if (Syscall.stat(parentDirectory, out Stat parentStat) != 0)
        {
            Logger.LogError("Unable to get status for {parentDirectory}.", parentDirectory);
        }

        UnixPermissionsConfig unixPermissionsConfig = permissionsConfig.Value with { };

        if (unixPermissionsConfig.UserId is null || unixPermissionsConfig.GroupId is null)
        {
            if (unixPermissionsConfig.UserId is not null)
            {
                Logger.LogDebug("Group ID not set for {path}, using parent's group ID", path);
                unixPermissionsConfig.GroupId = parentStat.st_gid;
            }
            else if (unixPermissionsConfig.GroupId is not null)
            {
                Logger.LogDebug("User ID not set for {path}, using parent's user ID", path);
                unixPermissionsConfig.UserId = parentStat.st_uid;
            }
            else
            {
                Logger.LogDebug(
                    "User ID and Group ID not set for {path}, using parent's user ID and group ID",
                    path
                );
                unixPermissionsConfig.UserId = parentStat.st_uid;
                unixPermissionsConfig.GroupId = parentStat.st_gid;
            }
        }

        // Change the owner and group of the file
        if (
            Syscall.chown(
                path,
                unixPermissionsConfig.UserId.Value,
                unixPermissionsConfig.GroupId.Value
            ) != 0
        )
        {
            Logger.LogError("Unable to change ownership of {path}.", path);
        }
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            return;
        }

        var pathSegments = path.Split(Path.DirectorySeparatorChar);
        var dirsInPath = Enumerable
            .Range(1, pathSegments.Length)
            .Select(i => Path.Combine(pathSegments.Take(i).ToArray()));

        string parentDirectory = Path.GetDirectoryName(path)!;

        foreach (var dir in dirsInPath)
        {
            if (Directory.Exists(dir))
            {
                parentDirectory = dir;
                continue;
            }

            break;
        }

        // Retrieve parent's permissions
        if (Syscall.stat(parentDirectory, out Stat parentStat) != 0)
        {
            Logger.LogWarning("Unable to get status for {parentDirectory}.", parentDirectory);
        }

        // Create the new directory with parent's permission mode
        if (Syscall.mkdir(path, parentStat.st_mode) != 0)
        {
            Logger.LogError("Unable to create directory {path}.", path);
        }

        UnixPermissionsConfig usedPermissions = permissionsConfig.Value with { };

        if (usedPermissions.UserId is null || usedPermissions.GroupId is null)
        {
            if (usedPermissions.UserId is not null)
            {
                Logger.LogDebug("Group ID not set for {path}, using parent's group ID", path);
                usedPermissions.GroupId = parentStat.st_gid;
            }
            else if (usedPermissions.GroupId is not null)
            {
                Logger.LogDebug("User ID not set for {path}, using parent's user ID", path);
                usedPermissions.UserId = parentStat.st_uid;
            }
            else
            {
                Logger.LogDebug(
                    "User ID and Group ID not set for {path}, using parent's user ID and group ID",
                    path
                );
                usedPermissions.UserId = parentStat.st_uid;
                usedPermissions.GroupId = parentStat.st_gid;
            }
        }

        var newDirsInPath = Path.GetRelativePath(parentDirectory, path)
            .Split(Path.DirectorySeparatorChar);
        var newSubdirs = Enumerable
            .Range(1, newDirsInPath.Length)
            .Select(i =>
                Path.Combine(
                    new[] { parentDirectory }.Concat(newDirsInPath.Take(i).ToArray()).ToArray()
                )
            );

        // Change the owner and group of the new directory
        foreach (var dir in newSubdirs)
        {
            if (
                Syscall.chown(dir, usedPermissions.UserId!.Value, usedPermissions.GroupId!.Value)
                != 0
            )
            {
                Logger.LogWarning("Unable to change ownership of {dir}.", dir);
            }
        }
    }

    /// <inheritdoc/>
    public void Move(string sourceFileName, string destFileName)
    {
        // Retrieve source file's permissions
        if (Syscall.stat(sourceFileName, out Stat sourceStat) != 0)
        {
            Logger.LogWarning("Unable to get status for {sourceFileName}.", sourceFileName);
        }

        File.Move(sourceFileName, destFileName);

        UnixPermissionsConfig usedPermissions = permissionsConfig.Value with { };

        // Apply configured permissions to the new file if set
        if (usedPermissions.UserId is null || usedPermissions.GroupId is null)
        {
            if (usedPermissions.UserId is not null)
            {
                Logger.LogDebug(
                    "Group ID not set for {destFileName}, using source's group ID",
                    destFileName
                );
                usedPermissions.GroupId = sourceStat.st_gid;
            }
            else if (usedPermissions.GroupId is not null)
            {
                Logger.LogDebug(
                    "User ID not set for {destFileName}, using source's user ID",
                    destFileName
                );
                usedPermissions.UserId = sourceStat.st_uid;
            }
            else
            {
                Logger.LogDebug(
                    "User ID and Group ID not set for {destFileName}, using source's user ID and group ID",
                    destFileName
                );
                usedPermissions.UserId = sourceStat.st_uid;
                usedPermissions.GroupId = sourceStat.st_gid;
            }
        }

        // Change the owner and group of the new file
        if (
            Syscall.chown(destFileName, usedPermissions.UserId.Value, usedPermissions.GroupId.Value)
            != 0
        )
        {
            Logger.LogWarning("Unable to change ownership of {destFileName}.", destFileName);
        }
    }
}
