using System.Text;

namespace MangaIngestWithUpscaling.Tests.Architecture;

/// <summary>
/// Guards the class of bug where a scratch directory or file is created under the system temp root
/// (<see cref="Path.GetTempPath" />) and left with the process umask's permissions — typically
/// world-readable — even though it holds a chapter's private page bytes. The canonical fix is
/// <c>DirectoryPermissions.CreateDirectoryRestricted</c> (for a directory) or
/// <c>DirectoryPermissions.RestrictToCurrentUser</c> (for a file), both in
/// <c>MangaIngestWithUpscaling.Shared.Services.FileSystem</c>.
/// </summary>
/// <remarks>
/// The test scans every <c>*.cs</c> and <c>*.razor</c> file under <c>src/</c>. A file that mentions
/// <see cref="Path.GetTempPath" /> outside comments must also call one of the two helpers, unless it
/// is named in <see cref="AllowedUnhardenedTempUsers" /> with a reason.
/// <para>
/// What it catches: a new site that references the temp root and does not harden. The rule is
/// intentionally broader than "creates something under the temp root", because statically proving
/// that a path derived from <see cref="Path.GetTempPath" /> is later written to is not reliable —
/// the page-assembly API sites build a temp file there through a helper rather than a literal
/// <c>Directory.CreateDirectory</c>. Over-approximating forces an explicit human decision (harden
/// or allowlist) for every temp reference.
/// </para>
/// <para>
/// What it cannot catch: a site that reaches the temp root without spelling
/// <see cref="Path.GetTempPath" /> (for example <c>Path.GetTempFileName</c>,
/// <c>Environment.GetEnvironmentVariable("TMPDIR")</c>, or a path handed in), and any unhardened
/// temp use inside a file that is already allowlisted. It also cannot tell whether the hardening
/// call actually applies to the temp path — only that the file knows the helper exists.
/// </para>
/// <para>
/// Comments are stripped with a C#-oriented scanner that preserves string literals, so a mention
/// inside a comment is ignored while one inside a string still counts (a string mention is worth
/// reviewing, so over-flagging there is deliberate). Razor markup is not parsed: a prose apostrophe
/// is treated as an ordinary character and cannot start a literal, and the only character-literal
/// special case is the quote-containing <c>'"'</c>.
/// </para>
/// </remarks>
public class TempScratchPermissionTests
{
    private const string TempRootReference = "Path.GetTempPath()";

    private static readonly string[] HardeningHelpers =
    {
        "DirectoryPermissions.CreateDirectoryRestricted",
        "DirectoryPermissions.RestrictToCurrentUser",
    };

    /// <summary>
    /// Files that reference <see cref="Path.GetTempPath" /> but legitimately do not harden it. Keep
    /// the list short and the reason specific: a new entry is a deliberate decision, not a shortcut.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedUnhardenedTempUsers = new(
        StringComparer.Ordinal
    )
    {
        // Sweeps stale scratch under the temp root (deletes only) and creates nothing there; its own
        // scratch lives in the models directory and is swept from there.
        ["src/MangaIngestWithUpscaling.Shared/Services/Upscaling/MangaJaNaiUpscaler.cs"] =
            "Only sweeps (deletes) stale scratch under the temp root; holds nothing private there.",

        // The temp CBZ is built beside its library destination and renamed onto it, so its mode
        // becomes the library deliverable's mode. The project manages library-deliverable ownership
        // and modes through IFileSystem/UnixPermissionsConfig; a hard-coded 0600 here would override
        // that and publish an owner-only library file. The Path.GetTempPath() fallback is
        // unreachable: the destination is a rooted library path, so Path.GetDirectoryName is never
        // null.
        ["src/MangaIngestWithUpscaling/Api/Upscaling/UpscalingDistributionService.Pages.cs"] =
            "Temp CBZ is a library-destination sibling whose mode is the library deliverable's; "
            + "the temp-root fallback is unreachable for a rooted library path.",
    };

    [Fact]
    public void TempRootScratchIsRestrictedToTheCurrentUser()
    {
        string repositoryRoot = FindRepositoryRoot();

        var offenders = new List<string>();
        foreach (string pattern in new[] { "*.cs", "*.razor" })
        {
            foreach (
                string file in Directory.EnumerateFiles(
                    Path.Combine(repositoryRoot, "src"),
                    pattern,
                    SearchOption.AllDirectories
                )
            )
            {
                if (IsInBuildOutput(file))
                {
                    continue;
                }

                string stripped = StripComments(File.ReadAllText(file));
                if (!stripped.Contains(TempRootReference, StringComparison.Ordinal))
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(repositoryRoot, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (AllowedUnhardenedTempUsers.ContainsKey(relativePath))
                {
                    continue;
                }

                if (
                    HardeningHelpers.Any(helper =>
                        stripped.Contains(helper, StringComparison.Ordinal)
                    )
                )
                {
                    continue;
                }

                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Files that reference Path.GetTempPath() must harden the scratch they create there with "
                + "DirectoryPermissions.CreateDirectoryRestricted (a directory) or "
                + "DirectoryPermissions.RestrictToCurrentUser (a file), or be allowlisted in "
                + nameof(AllowedUnhardenedTempUsers)
                + " with a reason. Offenders:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders)
        );
    }

    /// <summary>
    /// Removes line, block and Razor comments while preserving string and character literals, so a
    /// comment-only mention of <see cref="Path.GetTempPath" /> is not mistaken for a real use. String
    /// contents are kept deliberately: a mention inside a literal is still worth reviewing.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char current = source[i];

            if (current == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (current == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (current == '@' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '@'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            // A quote inside a character literal must not be read as the start of a string. Only
            // '"' can contain a quote, so it is the single case handled; every other character
            // literal is left as ordinary text (a Razor prose apostrophe must not swallow code).
            if (
                current == '\''
                && i + 2 < source.Length
                && source[i + 1] == '"'
                && source[i + 2] == '\''
            )
            {
                result.Append(source, i, 3);
                i += 3;
                continue;
            }

            if (current == '"')
            {
                result.Append(current);
                i++;
                // `$@"` and `@$"` are both verbatim: the character before the quote is `@`, or it is
                // `$` preceded by `@`.
                bool verbatim =
                    (i >= 2 && source[i - 2] == '@')
                    || (i >= 3 && source[i - 2] == '$' && source[i - 3] == '@');
                while (i < source.Length)
                {
                    char inner = source[i];
                    result.Append(inner);
                    i++;
                    if (!verbatim && inner == '\\' && i < source.Length)
                    {
                        result.Append(source[i]);
                        i++;
                        continue;
                    }

                    if (inner == '"')
                    {
                        if (verbatim && i < source.Length && source[i] == '"')
                        {
                            result.Append(source[i]);
                            i++;
                            continue;
                        }

                        break;
                    }
                }

                continue;
            }

            result.Append(current);
            i++;
        }

        return result.ToString();
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
