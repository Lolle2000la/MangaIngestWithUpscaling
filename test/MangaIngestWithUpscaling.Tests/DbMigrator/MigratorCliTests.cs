using MangaIngestWithUpscaling.DbMigrator;

namespace MangaIngestWithUpscaling.Tests.DbMigrator;

public class MigratorCliTests
{
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
