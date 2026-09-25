using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.DbMigrator;
using Microsoft.Data.Sqlite;

namespace MangaIngestWithUpscaling.Tests.DbMigrator;

public class MigratorCliTests
{
    [Fact]
    public void Redact_RemovesConnectionStringsAndInlinePasswords()
    {
        var options = new MigratorOptions(
            DatabaseProvider.Postgres,
            "Host=db;Username=u;Password=supersecret",
            DatabaseProvider.Sqlite,
            "Data Source=/tmp/x.db",
            BatchSize: 500,
            Force: false,
            IncludeLogs: false,
            FromLogsConnection: null,
            ToLogsConnection: null
        );

        string redacted = MigratorCli.Redact(
            "Npgsql failed: Host=db;Username=u;Password=supersecret and Password=other",
            options
        );

        Assert.DoesNotContain("supersecret", redacted);
        Assert.DoesNotContain("other", redacted);
        Assert.Contains("Password=***", redacted);
    }

    [Theory]
    [InlineData("Password=supersecret", "Password=***")]
    [InlineData("Pwd=letmein", "Pwd=***")]
    [InlineData("Password=\"my secret\"", "Password=***")]
    [InlineData("Pwd='top secret'", "Pwd=***")]
    [InlineData("password=spaced value here", "password=***")]
    // Connection-string builders escape an embedded quote by doubling it.
    [InlineData("Password=\"a\"\"b\"", "Password=***")]
    [InlineData("Pwd='a''b'", "Pwd=***")]
    public void Redact_RemovesInlinePasswordValuesRegardlessOfQuoting(string input, string expected)
    {
        var options = OptionsWithConnection("Data Source=/tmp/migrator-redact-unrelated.db");

        string redacted = MigratorCli.Redact(input, options);

        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void Redact_PreservesKeyCasing()
    {
        var options = OptionsWithConnection("Data Source=/tmp/migrator-redact-unrelated.db");

        Assert.Contains("Password=***", MigratorCli.Redact("Password=secret", options));
        Assert.Contains("password=***", MigratorCli.Redact("password=secret", options));
    }

    [Fact]
    public async Task Main_MissingRequiredArguments_ReturnsNonZero() =>
        Assert.NotEqual(0, await MigratorCli.Main([]));

    [Fact]
    public async Task Main_UnknownProvider_ReturnsNonZero()
    {
        int exitCode = await MigratorCli.Main([
            "--from",
            "sqlite",
            "--to",
            "bogus",
            "--from-connection",
            "Data Source=/tmp/from.db",
            "--to-connection",
            "Data Source=/tmp/to.db",
        ]);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_ExplicitFalseFlags_AreAccepted()
    {
        MigratorOptions? captured = null;

        int exitCode = await MigratorCli.RunAsync(
            [
                "--from",
                "sqlite",
                "--to",
                "postgres",
                "--from-connection",
                "from",
                "--to-connection",
                "to",
                "--force=false",
                "--include-logs=false",
            ],
            (options, _, _) =>
            {
                captured = options;
                return Task.CompletedTask;
            }
        );

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.False(captured!.Force);
        Assert.False(captured.IncludeLogs);
    }

    [Fact]
    public async Task RunAsync_Sqlite3Alias_IsAcceptedForParityWithTheApplication()
    {
        MigratorOptions? captured = null;

        int exitCode = await MigratorCli.RunAsync(
            [
                "--from",
                "sqlite3",
                "--to",
                "postgres",
                "--from-connection",
                "from",
                "--to-connection",
                "to",
            ],
            (options, _, _) =>
            {
                captured = options;
                return Task.CompletedTask;
            }
        );

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(DatabaseProvider.Sqlite, captured!.FromProvider);
        Assert.Equal(DatabaseProvider.Postgres, captured.ToProvider);
    }

    [Fact]
    public async Task Migrate_SqliteSourceFileMissing_FailsWithoutCreatingTheFile()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"db-migrator-missing-{Guid.NewGuid():N}.db"
        );

        try
        {
            var options = new MigratorOptions(
                DatabaseProvider.Sqlite,
                new SqliteConnectionStringBuilder { DataSource = path }.ToString(),
                DatabaseProvider.Postgres,
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                BatchSize: 500,
                Force: false,
                IncludeLogs: false,
                FromLogsConnection: null,
                ToLogsConnection: null
            );

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DataMigrator.MigrateAsync(
                        options,
                        _ => { },
                        TestContext.Current.CancellationToken
                    )
                );

            Assert.Contains(path, exception.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ResolveSqliteFile_PercentDecodesFileUriPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"db-migrator-uri-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "my data.db");
        File.WriteAllBytes(path, []);
        try
        {
            // A valid SQLite source can be a "file:" URI whose path is percent-escaped; it must be
            // decoded before the existence check or an existing database is reported missing.
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = "file:" + path.Replace(" ", "%20"),
            }.ToString();

            (string Path, bool Exists)? resolved = DataMigrator.ResolveSqliteFile(connectionString);

            Assert.NotNull(resolved);
            Assert.Equal(path, resolved!.Value.Path);
            Assert.True(resolved.Value.Exists);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NormalizeUtcDateTimes_ConvertsLocalAndOffsetValuesToUtc()
    {
        var local = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Local);
        var unspecified = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
        var offset = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));

        var holder = new TimestampHolder
        {
            When = local,
            MaybeWhen = unspecified,
            Offset = offset,
        };

        DataMigrator.NormalizeUtcDateTimes(holder);

        Assert.Equal(DateTimeKind.Utc, holder.When.Kind);
        Assert.Equal(local.ToUniversalTime(), holder.When);

        // Unspecified values are relabelled, not shifted: the ticks must be preserved.
        Assert.Equal(DateTimeKind.Utc, holder.MaybeWhen!.Value.Kind);
        Assert.Equal(unspecified.Ticks, holder.MaybeWhen.Value.Ticks);

        Assert.Equal(offset.ToUniversalTime(), holder.Offset);
        Assert.Equal(TimeSpan.Zero, holder.Offset.Offset);
    }

    private sealed class TimestampHolder
    {
        public DateTime When { get; set; }

        public DateTime? MaybeWhen { get; set; }

        public DateTimeOffset Offset { get; set; }
    }

    [Theory]
    [InlineData("--force", "force", true)]
    [InlineData("--force=true", "force", true)]
    [InlineData("--force=false", "force", false)]
    [InlineData("--force=0", "force", false)]
    [InlineData("--include-logs", "include-logs", true)]
    [InlineData("--include-logs=false", "include-logs", false)]
    public void IsFlagSet_ParsesExplicitBooleanValues(string arg, string name, bool expected)
    {
        Dictionary<string, string> options = MigratorCli.ParseArgs([arg]);
        Assert.Equal(expected, MigratorCli.IsFlagSet(options, name));
    }

    [Fact]
    public void IsFlagSet_SpaceSeparatedFalse_IsUnset()
    {
        Dictionary<string, string> options = MigratorCli.ParseArgs(["--force", "false"]);
        Assert.False(MigratorCli.IsFlagSet(options, "force"));
    }

    [Fact]
    public void IsFlagSet_AbsentFlag_IsUnset() =>
        Assert.False(MigratorCli.IsFlagSet(MigratorCli.ParseArgs([]), "force"));

    private static MigratorOptions OptionsWithConnection(string fromConnection) =>
        new(
            DatabaseProvider.Postgres,
            fromConnection,
            DatabaseProvider.Sqlite,
            "Data Source=/tmp/migrator-redact-target.db",
            BatchSize: 500,
            Force: false,
            IncludeLogs: false,
            FromLogsConnection: null,
            ToLogsConnection: null
        );
}
