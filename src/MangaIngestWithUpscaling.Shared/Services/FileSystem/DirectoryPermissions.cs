using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.FileSystem;

/// <summary>
/// Best-effort permission hardening for paths the app creates to hold private library bytes (the page
/// spool, preprocessing scratch, and the streamed worker's work/source/output directories).
/// <see cref="Directory.CreateDirectory" /> and the zip writer honour the process umask (typically
/// 022, so world-readable), which would otherwise expose a chapter's pages to other users on the host.
/// </summary>
public static class DirectoryPermissions
{
    private static int _warnedFailure;

    /// <summary>
    /// Restricts a path the app created to the current user (owner read/write/execute). On Windows
    /// this is a no-op, and on a filesystem without Unix-mode support the failure is swallowed: the
    /// deployment is Linux containers, and a permission tightening must not fail a chapter.
    /// <para>
    /// A failed chmod leaves the path world-readable, which is exactly the exposure this guard exists
    /// to prevent, so the failure is surfaced at Warning — but only once per process: the caller runs
    /// this on every page/spool write, and a filesystem that cannot set modes would otherwise flood
    /// the log. Pass the caller's logger so the signal is not lost; a missing logger stays silent,
    /// as the helper is also used from paths that have no logger handy.
    /// </para>
    /// </summary>
    public static void RestrictToCurrentUser(string path, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        catch (Exception ex)
        {
            // Best effort only, but a persistent failure is actionable: the path holds private bytes.
            if (logger is not null && Interlocked.Exchange(ref _warnedFailure, 1) == 0)
            {
                logger.LogWarning(
                    ex,
                    "Could not restrict {Path} to the current user; it may be readable by other users on this host.",
                    path
                );
            }
        }
    }
}
