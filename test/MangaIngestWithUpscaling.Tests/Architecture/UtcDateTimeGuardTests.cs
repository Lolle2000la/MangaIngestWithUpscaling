using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Tests.Architecture;

/// <summary>
/// Guards the UTC assumption of PostgreSQL's <c>timestamp with time zone</c> columns. SQLite accepts
/// any <see cref="DateTime"/> kind, but Npgsql rejects a non-UTC one at runtime, so code that works
/// against SQLite can break in production. The codebase uses <c>DateTime.UtcNow</c>; this test stops
/// the local-wall-clock habit from creeping back in. It scans <c>src/</c> and <c>tools/</c>.
/// </summary>
public class UtcDateTimeGuardTests
{
    [Fact]
    public void SourceDoesNotUseLocalWallClockTime()
    {
        string repositoryRoot = FindRepositoryRoot();

        var offenders = new List<string>();
        foreach (
            string sourceRoot in new[]
            {
                Path.Combine(repositoryRoot, "src"),
                Path.Combine(repositoryRoot, "tools"),
            }.Where(Directory.Exists)
        )
        {
            foreach (
                string file in Directory.EnumerateFiles(
                    sourceRoot,
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            {
                if (IsInBuildOutput(file))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                foreach (
                    Match match in Regex.Matches(
                        text,
                        @"\bDateTime\.Now\b|\bDateTime\.Today\b|\bDateTimeOffset\.Now\b"
                    )
                )
                {
                    offenders.Add($"{Path.GetRelativePath(repositoryRoot, file)}: {match.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Use UTC (DateTime.UtcNow / DateTimeOffset.UtcNow) instead of local wall-clock time; "
                + "PostgreSQL timestamp with time zone rejects non-UTC values:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders)
        );
    }

    private static bool IsInBuildOutput(string file) =>
        file.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal
        )
        || file.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal
        );

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaIngestWithUpscaling.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (MangaIngestWithUpscaling.sln) from "
                + AppContext.BaseDirectory
        );
    }
}
