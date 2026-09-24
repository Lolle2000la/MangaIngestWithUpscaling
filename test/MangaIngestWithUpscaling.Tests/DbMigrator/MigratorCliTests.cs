using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.DbMigrator;

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
}
