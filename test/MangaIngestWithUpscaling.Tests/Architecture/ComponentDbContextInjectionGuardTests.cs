using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Tests.Architecture;

/// <summary>
/// Guards the per-operation DbContext invariant for Blazor components. A circuit-scoped
/// <c>ApplicationDbContext</c> shared by every component, dialog and scoped service lets overlapping
/// operations collide ("A second operation was started on this context instance") and keeps every
/// entity it ever loaded tracked for the life of the circuit. Components must instead inject
/// <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> and open a context per operation.
/// </summary>
public class ComponentDbContextInjectionGuardTests
{
    /// <summary>
    /// Matches direct construction of the context type through the common injection/service-locator
    /// shapes. An alias (`@using AppDb = ...ApplicationDbContext`) is still possible but is
    /// deliberately out of scope; this catches the realistic regressions.
    /// </summary>
    private static readonly Regex[] ForbiddenPatterns =
    [
        // @inject ApplicationDbContext DbContext / @inject global::...ApplicationDbContext
        new(@"@inject\s+([A-Za-z_][\w.]*::)?ApplicationDbContext\b", RegexOptions.Compiled),
        // [Inject] public ApplicationDbContext DbContext { get; set; }
        new(
            @"\[Inject\][^\n]*\bApplicationDbContext\b",
            RegexOptions.Compiled | RegexOptions.Singleline
        ),
        // GetRequiredService<ApplicationDbContext>() / GetService<ApplicationDbContext>()
        new(
            @"Get(Required)?Service\s*<\s*([A-Za-z_][\w.]*::)?ApplicationDbContext\s*>",
            RegexOptions.Compiled
        ),
        // GetRequiredService(typeof(ApplicationDbContext)) / GetService(typeof(ApplicationDbContext))
        new(
            @"Get(Required)?Service\s*\(\s*typeof\s*\(\s*([A-Za-z_][\w.]*::)?ApplicationDbContext\s*\)",
            RegexOptions.Compiled
        ),
    ];

    [Fact]
    public void ComponentsDoNotUseTheSharedApplicationDbContext()
    {
        string repositoryRoot = FindRepositoryRoot();
        string componentsRoot = Path.Combine(
            repositoryRoot,
            "src",
            "MangaIngestWithUpscaling",
            "Components"
        );

        var offenders = new List<string>();
        foreach (string pattern in new[] { "*.razor", "*.razor.cs" })
        {
            foreach (
                string file in Directory.EnumerateFiles(
                    componentsRoot,
                    pattern,
                    SearchOption.AllDirectories
                )
            )
            {
                string text = File.ReadAllText(file);
                foreach (Regex regex in ForbiddenPatterns)
                {
                    foreach (Match match in regex.Matches(text))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(repositoryRoot, file)}: {match.Value.Trim()}"
                        );
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Blazor components must not use a shared ApplicationDbContext. Inject "
                + "IDbContextFactory<ApplicationDbContext> and open a context per operation instead:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders)
        );
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
