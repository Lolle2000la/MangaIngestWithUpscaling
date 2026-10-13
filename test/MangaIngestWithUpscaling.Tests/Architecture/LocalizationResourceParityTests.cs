using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MangaIngestWithUpscaling.Tests.Architecture;

/// <summary>
/// Guards the localization resource files of the web app against key drift. Every culture of the same
/// component (the same file name without its culture suffix, in the same directory) must expose
/// exactly the same set of keys. A key present in only one culture makes that culture render the raw
/// key name once it is looked up through <c>IStringLocalizer</c>.
/// </summary>
public class LocalizationResourceParityTests
{
    // The trailing segment of one of `Component.en-US.resx`, `Component.de-DE.resx`, ... — a culture
    // name such as `en-US`, `de-DE`, `ja-JP`, `zh-Hans` or `sr-Latn-RS`. The base name may itself
    // contain dots, so the suffix is matched specifically rather than by splitting on the last dot.
    private static readonly Regex CultureSuffix = new(
        @"^(?<base>.+)\.(?<culture>[a-z]{2,3}(?:-[A-Za-z0-9]+)+)$",
        RegexOptions.Compiled
    );

    [Fact]
    public void EveryCultureOfAComponentExposesTheSameKeys()
    {
        string repositoryRoot = FindRepositoryRoot();
        string resourcesRoot = Path.Combine(
            repositoryRoot,
            "src",
            "MangaIngestWithUpscaling",
            "Resources"
        );

        Assert.True(
            Directory.Exists(resourcesRoot),
            $"Could not find the app's Resources directory at {resourcesRoot}"
        );

        var groups =
            new Dictionary<(string Directory, string BaseName), Dictionary<string, List<string>>>();
        foreach (
            string file in Directory.EnumerateFiles(
                resourcesRoot,
                "*.resx",
                SearchOption.AllDirectories
            )
        )
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            Match match = CultureSuffix.Match(stem);
            string baseName = match.Success ? match.Groups["base"].Value : stem;
            string culture = match.Success ? match.Groups["culture"].Value : "(neutral)";
            string directory = Path.GetDirectoryName(file)!;

            var group = groups.GetValueOrDefault((directory, baseName));
            if (group is null)
            {
                group = new Dictionary<string, List<string>>();
                groups[(directory, baseName)] = group;
            }

            group[culture] = ReadKeys(file);
        }

        var offenders = new List<string>();
        foreach (((string directory, string baseName), var cultures) in groups)
        {
            string location = Path.GetRelativePath(
                repositoryRoot,
                Path.Combine(directory, baseName)
            );

            var keyUnion = cultures
                .Values.SelectMany(keys => keys)
                .ToHashSet(StringComparer.Ordinal);
            if (keyUnion.Count == 0)
            {
                offenders.Add($"{location}: no keys in any culture");
                continue;
            }

            foreach ((string culture, List<string> keys) in cultures)
            {
                var missing = keyUnion
                    .Except(keys, StringComparer.Ordinal)
                    .OrderBy(k => k)
                    .ToList();
                if (missing.Count > 0)
                {
                    offenders.Add($"{location} [{culture}]: missing {string.Join(", ", missing)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Resource files of the same component must expose the same keys in every culture:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders)
        );
    }

    private static List<string> ReadKeys(string file)
    {
        XDocument document = XDocument.Load(file);
        return document
            .Root!.Elements("data")
            .Select(element => element.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

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
