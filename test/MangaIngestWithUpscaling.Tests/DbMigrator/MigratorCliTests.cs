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
    [InlineData("Password=supersecret", "supersecret")]
    [InlineData("Pwd=letmein", "letmein")]
    [InlineData("Password=\"my secret\"", "my secret")]
    [InlineData("Pwd='top secret'", "top secret")]
    [InlineData("password=spaced value here", "spaced value here")]
    public void Redact_RemovesInlinePasswordValuesRegardlessOfQuoting(string input, string secret)
    {
        var options = OptionsWithConnection("Data Source=/tmp/migrator-redact-unrelated.db");

        string redacted = MigratorCli.Redact(input, options);

        Assert.DoesNotContain(secret, redacted);
        Assert.Matches(@"(?i)(password|pwd)=\*\*\*", redacted);
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
