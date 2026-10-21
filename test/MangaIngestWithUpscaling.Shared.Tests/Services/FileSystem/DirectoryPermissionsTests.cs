using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.FileSystem;

public class DirectoryPermissionsTests
{
    // These tests force a chmod failure with a path that does not exist (FileNotFoundException), so they
    // exercise the Unix-only branch and are meaningless on Windows.
    private static string MissingPath() =>
        Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}");

    [Fact]
    [Trait("Category", "Unit")]
    public void RestrictToCurrentUser_FirstFailure_Warns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new RecordingLogger();

        DirectoryPermissions.RestrictToCurrentUser(MissingPath(), logger);

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RestrictToCurrentUser_RepeatedSameFailure_WarnsOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new RecordingLogger();
        string path = MissingPath();

        DirectoryPermissions.RestrictToCurrentUser(path, logger);
        DirectoryPermissions.RestrictToCurrentUser(path, logger);
        DirectoryPermissions.RestrictToCurrentUser(path, logger);

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RestrictToCurrentUser_SecondDifferentPath_StillWarns()
    {
        // The old guard was a single process-global token: the first failure — possibly a transient
        // one — consumed it and a later, different failure (another path, or a read-only filesystem)
        // was never reported. Keying by failure means the second path still warns.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new RecordingLogger();

        DirectoryPermissions.RestrictToCurrentUser(MissingPath(), logger);
        DirectoryPermissions.RestrictToCurrentUser(MissingPath(), logger);

        Assert.Equal(2, logger.Warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CreateDirectoryRestricted_CreatesAnOwnerOnlyDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"restricted_{Guid.NewGuid():N}");
        try
        {
            DirectoryPermissions.CreateDirectoryRestricted(path, new RecordingLogger());

            Assert.True(Directory.Exists(path));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path)
            );
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CreateDirectoryRestricted_TightensAnAlreadyExistingPermissiveDirectory()
    {
        // The explicit-mode overload only applies the mode when it creates the directory, so an
        // existing path is fixed by the follow-up restriction instead.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"existing_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.OtherRead
            );

            DirectoryPermissions.CreateDirectoryRestricted(path, new RecordingLogger());

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path)
            );
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>Counts Warning-level calls, the signal the helper's best-effort guard produces.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
