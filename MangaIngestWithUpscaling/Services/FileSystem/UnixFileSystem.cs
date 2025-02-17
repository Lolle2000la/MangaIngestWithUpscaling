﻿using MangaIngestWithUpscaling.Configuration;
using Microsoft.Extensions.Options;
using Mono.Unix.Native;

namespace MangaIngestWithUpscaling.Services.FileSystem;

public class UnixFileSystem(
    IOptions<UnixPermissionsConfig> permissionsConfig,
    ILogger<UnixFileSystem> logger) : IFileSystem
{
    public void ApplyPermissions(string path)
    {
        path = Path.GetFullPath(path);
        string? parentDirectory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Unable to get parent directory for {path}");

        // Retrieve parent's permissions
        if (Syscall.stat(parentDirectory, out Stat parentStat) != 0)
        {
            logger.LogError("Unable to get status for {parentDirectory}.", parentDirectory);
        }

        UnixPermissionsConfig unixPermissionsConfig = permissionsConfig.Value with { };

        if (!unixPermissionsConfig.UserId.HasValue || !unixPermissionsConfig.GroupId.HasValue)
        {
            if (unixPermissionsConfig.UserId.HasValue)
            {
                logger.LogDebug("Group ID not set for {path}, using parent's group ID", path);
                unixPermissionsConfig.GroupId = parentStat.st_gid;
            }
            else if (unixPermissionsConfig.GroupId.HasValue)
            {
                logger.LogDebug("User ID not set for {path}, using parent's user ID", path);
                unixPermissionsConfig.UserId = parentStat.st_uid;
            }
            else
            {
                logger.LogDebug("User ID and Group ID not set for {path}, using parent's user ID and group ID", path);
                unixPermissionsConfig.UserId = parentStat.st_uid;
                unixPermissionsConfig.GroupId = parentStat.st_gid;
            }
        }

        // Change the owner and group of the file
        if (Syscall.chown(path, unixPermissionsConfig.UserId.Value, unixPermissionsConfig.GroupId.Value) != 0)
        {
            logger.LogError("Unable to change ownership of {path}.", path);
        }
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string? parentDirectory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Unable to get parent directory for {path}");

        // Retrieve parent's permissions
        if (Syscall.stat(parentDirectory, out Stat parentStat) != 0)
        {
            logger.LogWarning("Unable to get status for {parentDirectory}.", parentDirectory);
        }

        // Create the new directory with parent's permission mode
        if (Syscall.mkdir(path, parentStat.st_mode) != 0)
        {
            logger.LogError("Unable to create directory {path}.", path);
        }

        UnixPermissionsConfig usedPermissions = permissionsConfig.Value with { };

        if (!usedPermissions.UserId.HasValue || !usedPermissions.GroupId.HasValue)
        {
            if (usedPermissions.UserId.HasValue)
            {
                logger.LogDebug("Group ID not set for {path}, using parent's group ID", path);
                usedPermissions.GroupId = parentStat.st_gid;
            }
            else if (usedPermissions.GroupId.HasValue)
            {
                logger.LogDebug("User ID not set for {path}, using parent's user ID", path);
                usedPermissions.UserId = parentStat.st_uid;
            }
            else
            {
                logger.LogDebug("User ID and Group ID not set for {path}, using parent's user ID and group ID", path);
                usedPermissions.UserId = parentStat.st_uid;
                usedPermissions.GroupId = parentStat.st_gid;
            }
        }

        // Change the owner and group of the new directory
        foreach (var dir in Path.GetRelativePath(parentDirectory, path).Split(Path.DirectorySeparatorChar))
        {
            if (Syscall.chown(dir, usedPermissions.UserId!.Value, usedPermissions.GroupId!.Value) != 0)
            {
                logger.LogWarning("Unable to change ownership of {dir}.", dir);
            }
        }
    }

    /// <inheritdoc/>
    public void Move(string sourceFileName, string destFileName)
    {
        // Retrieve source file's permissions
        if (Syscall.stat(sourceFileName, out Stat sourceStat) != 0)
        {
            logger.LogWarning("Unable to get status for {sourceFileName}.", sourceFileName);
        }

        File.Move(sourceFileName, destFileName);

        UnixPermissionsConfig usedPermissions = permissionsConfig.Value with { };

        // Apply configured permissions to the new file if set
        if (!usedPermissions.UserId.HasValue || !usedPermissions.GroupId.HasValue)
        {
            if (usedPermissions.UserId.HasValue)
            {
                logger.LogDebug("Group ID not set for {destFileName}, using source's group ID", destFileName);
                usedPermissions.GroupId = sourceStat.st_gid;
            }
            else if (usedPermissions.GroupId.HasValue)
            {
                logger.LogDebug("User ID not set for {destFileName}, using source's user ID", destFileName);
                usedPermissions.UserId = sourceStat.st_uid;
            }
            else
            {
                logger.LogDebug("User ID and Group ID not set for {destFileName}, using source's user ID and group ID", destFileName);
                usedPermissions.UserId = sourceStat.st_uid;
                usedPermissions.GroupId = sourceStat.st_gid;
            }
        }

        // Change the owner and group of the new file
        if (Syscall.chown(destFileName, usedPermissions.UserId.Value, usedPermissions.GroupId.Value) != 0)
        {
            logger.LogWarning("Unable to change ownership of {destFileName}.", destFileName);
        }
    }
}
