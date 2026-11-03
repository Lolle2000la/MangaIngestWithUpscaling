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
    /// <summary>Owner read/write/execute, the mode every private path is restricted to.</summary>
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    ///     How many distinct failures are remembered before the warning set is cleared. The set is only
    ///     ever a warning de-duplicator, so dropping remembered keys after this many distinct failures is
    ///     safe — and keeps a long-running process with a new scratch path per chapter from growing it
    ///     without bound. Same bounded-clear pattern as the model verdict caches.
    /// </summary>
    private const int MaxRememberedFailures = 64;

    private static readonly object WarnedGate = new();
    private static readonly HashSet<string> WarnedFailures = new(StringComparer.Ordinal);

    /// <summary>
    ///     Creates <paramref name="path" /> (and any missing parents) owner-only where the platform and
    ///     API allow it. <see cref="Directory.CreateDirectory(string, UnixFileMode)" /> sets the mode as
    ///     the directory is created, closing the window in which the post-hoc
    ///     <see cref="RestrictToCurrentUser" /> would leave it umask-permissive. The explicit restriction
    ///     still runs afterwards, because that overload applies the mode only when it actually creates
    ///     the directory — a path that already exists (or a platform without the overload) would
    ///     otherwise stay permissive.
    /// </summary>
    public static void CreateDirectoryRestricted(string path, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(path, OwnerOnly);
            }
            catch (PlatformNotSupportedException)
            {
                // No explicit-mode overload here; create plainly and tighten below.
                Directory.CreateDirectory(path);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        RestrictToCurrentUser(path, logger);
    }

    /// <summary>
    /// Restricts a path the app created to the current user (owner read/write/execute). On Windows
    /// this is a no-op, and on a filesystem without Unix-mode support the failure is swallowed: the
    /// deployment is Linux containers, and a permission tightening must not fail a chapter.
    /// <para>
    /// A failed chmod leaves the path world-readable, which is exactly the exposure this guard exists
    /// to prevent, so the failure is surfaced at Warning — but only once per *failure*, not once per
    /// process: the caller runs this on every page/spool write, so a filesystem that cannot set modes
    /// would otherwise flood the log, while a first (possibly transient) failure must not silence a
    /// later, different one (another path, or another exception kind such as a read-only filesystem).
    /// The key is the path plus the exception type, and the remembered set is bounded. Pass the
    /// caller's logger so the signal is not lost; a missing logger stays silent, as the helper is also
    /// used from paths that have no logger handy.
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
            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception ex)
        {
            // Best effort only, but a persistent failure is actionable: the path holds private bytes.
            if (logger is null)
            {
                return;
            }

            string key = $"{path}\n{ex.GetType().FullName}";
            lock (WarnedGate)
            {
                if (WarnedFailures.Count >= MaxRememberedFailures && !WarnedFailures.Contains(key))
                {
                    WarnedFailures.Clear();
                }

                if (!WarnedFailures.Add(key))
                {
                    return;
                }
            }

            logger.LogWarning(
                ex,
                "Could not restrict {Path} to the current user; it may be readable by other users on this host.",
                path
            );
        }
    }
}
