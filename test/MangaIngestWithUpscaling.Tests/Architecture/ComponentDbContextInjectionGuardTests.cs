using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Tests.Architecture;

/// <summary>
/// Guards the per-operation DbContext invariant for Blazor components. A circuit-scoped
/// <c>ApplicationDbContext</c> (or <c>LoggingDbContext</c>) shared by every component, dialog and
/// scoped service lets overlapping operations collide ("A second operation was started on this
/// context instance") and keeps every entity it ever loaded tracked for the life of the circuit.
/// Components must instead inject <c>IDbContextFactory&lt;TContext&gt;</c> and open a context per
/// operation.
/// </summary>
public class ComponentDbContextInjectionGuardTests
{
    /// <summary>
    /// Matches a bare <c>*DbContext</c> type, optionally namespace-qualified (including an alias
    /// qualifier such as <c>global::</c>). A named capture is deliberately not used: the trailing
    /// context below pins the token so a context type used as a generic argument
    /// (<c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>) does not trip the guard.
    /// </summary>
    private const string ContextType = @"(?:[A-Za-z_][\w.:]*\.)?\w+DbContext";

    /// <summary>
    /// Matches direct use of a context type through the common injection/service-locator shapes. An
    /// alias (<c>@using AppDb = ...ApplicationDbContext</c>) is still possible but is deliberately
    /// out of scope; this catches the realistic regressions.
    /// </summary>
    private static readonly Regex[] ForbiddenPatterns =
    [
        // @inject ApplicationDbContext DbContext / @inject global::...LoggingDbContext DbContext
        new($@"@inject\s+{ContextType}\??\s+[A-Za-z_]\w*", RegexOptions.Compiled),
        // [Inject] public ApplicationDbContext DbContext { get; set; }
        new($@"\[Inject\][^\n]*\b{ContextType}\??\s+[A-Za-z_]\w*", RegexOptions.Compiled),
        // GetRequiredService<ApplicationDbContext>() / GetService<LoggingDbContext>()
        new($@"Get(?:Required)?Service\s*<\s*{ContextType}\s*>", RegexOptions.Compiled),
        // GetRequiredService(typeof(ApplicationDbContext)) / GetService(typeof(LoggingDbContext))
        new(
            $@"Get(?:Required)?Service\s*\(\s*typeof\s*\(\s*{ContextType}\s*\)",
            RegexOptions.Compiled
        ),
    ];

    [Fact]
    public void ComponentsDoNotUseASharedDbContext()
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
            "Blazor components must not use a shared DbContext. Inject "
                + "IDbContextFactory<TContext> and open a context per operation instead:"
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
