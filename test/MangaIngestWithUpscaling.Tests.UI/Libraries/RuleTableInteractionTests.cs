using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.Libraries.Filters;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

/// <summary>
/// Interaction tests for the inline rule tables in <see cref="EditLibraryFilters"/> and
/// <see cref="EditLibraryRenames"/>. The existing assertions only cover the model collections and
/// the plain <c>RowTemplate</c>; these drive the header Add button, the row-edit template (enter,
/// change a field, commit) and the row Delete button so a broken render/commit fails the suite.
/// </summary>
public class RuleTableInteractionTests : LibraryTestBase
{
    // ------------------------------------------------------------------ filters

    [Fact]
    public async Task FilterTable_Add_AppendsRowWithEnumDefaults()
    {
        Library library = NewLibrary();
        LibraryFilterRule seeded = NewFilterRule(
            library,
            "seeded",
            LibraryFilterPatternType.Contains,
            LibraryFilterTargetField.MangaTitle,
            FilterAction.Exclude
        );
        library.FilterRules = [seeded];

        IRenderedComponent<EditLibraryFilters> component = RenderWithProviders<EditLibraryFilters>(
            parameters => parameters.Add(p => p.Library, library)
        );

        Assert.Single(DataRows(component));

        await ClickHeaderAddAsync(component);

        component.WaitForAssertion(() => Assert.Equal(2, DataRows(component).Count));
        Assert.Equal(2, library.FilterRules.Count);

        LibraryFilterRule added = library.FilterRules[1];
        Assert.Equal(LibraryFilterPatternType.Regex, added.PatternType);
        Assert.Equal(LibraryFilterTargetField.FilePath, added.TargetField);
        Assert.Equal(FilterAction.Include, added.Action);

        // The new row renders those defaults (the seeded row uses non-default enum values).
        FindDataRow(component, "Enum_PatternType_Regex");
        FindDataRow(component, "Enum_TargetField_FilePath");
        FindDataRow(component, "Enum_Action_Include");
    }

    [Fact]
    public async Task FilterTable_EditRow_CommitsValueAndUpdatesModel()
    {
        Library library = NewLibrary();
        LibraryFilterRule rule = NewFilterRule(
            library,
            "old-pattern",
            LibraryFilterPatternType.Contains,
            LibraryFilterTargetField.MangaTitle,
            FilterAction.Include
        );
        library.FilterRules = [rule];

        IRenderedComponent<EditLibraryFilters> component = RenderWithProviders<EditLibraryFilters>(
            parameters => parameters.Add(p => p.Library, library)
        );

        await EnterEditAsync(component, FindDataRow(component, "old-pattern"));

        IRenderedComponent<MudTextField<string>> patternField = component
            .FindComponents<MudTextField<string>>()
            .Single();
        await component.InvokeAsync(() =>
            patternField.Instance.ValueChanged.InvokeAsync("new-pattern")
        );

        await CommitAsync(component);

        component.WaitForAssertion(() =>
        {
            Assert.Equal("new-pattern", rule.Pattern);
            IElement updated = FindDataRow(component, "new-pattern");
            Assert.Contains("new-pattern", updated.TextContent);
        });

        // Committing leaves edit mode.
        Assert.Empty(component.FindAll("button[aria-label='Commit edit']"));
    }

    [Fact]
    public async Task FilterTable_Delete_RemovesRowAndModelEntry()
    {
        Library library = NewLibrary();
        LibraryFilterRule rule = NewFilterRule(
            library,
            "seeded",
            LibraryFilterPatternType.Contains,
            LibraryFilterTargetField.MangaTitle,
            FilterAction.Include
        );
        library.FilterRules = [rule];

        IRenderedComponent<EditLibraryFilters> component = RenderWithProviders<EditLibraryFilters>(
            parameters => parameters.Add(p => p.Library, library)
        );

        IElement row = FindDataRow(component, "seeded");
        IElement delete = row.QuerySelectorAll("button").Single();
        await component.InvokeAsync(() => delete.Click(new MouseEventArgs()));

        component.WaitForAssertion(() => Assert.Empty(DataRows(component)));
        Assert.Empty(library.FilterRules);
    }

    // ------------------------------------------------------------------ renames

    [Fact]
    public async Task RenameTable_Add_AppendsRowWithEnumDefaultsAndFiresRulesChanged()
    {
        Library library = NewLibrary();
        LibraryRenameRule seeded = NewRenameRule(
            library,
            "seeded",
            LibraryRenamePatternType.Contains,
            LibraryRenameTargetField.FileName,
            "seeded-replacement"
        );
        library.RenameRules = new ObservableCollection<LibraryRenameRule> { seeded };

        int rulesChanged = 0;
        EventCallback rulesChangedCallback = EventCallback.Factory.Create(
            this,
            () => rulesChanged++
        );

        IRenderedComponent<EditLibraryRenames> component = RenderWithProviders<EditLibraryRenames>(
            parameters =>
            {
                parameters.Add(p => p.Library, library);
                parameters.Add(p => p.RulesChanged, rulesChangedCallback);
            }
        );

        Assert.Single(DataRows(component));

        await ClickHeaderAddAsync(component);

        component.WaitForAssertion(() => Assert.Equal(2, DataRows(component).Count));
        Assert.Equal(2, library.RenameRules.Count);
        Assert.Equal(LibraryRenamePatternType.Regex, library.RenameRules[1].PatternType);
        Assert.Equal(LibraryRenameTargetField.SeriesTitle, library.RenameRules[1].TargetField);
        Assert.Equal(1, rulesChanged);

        FindDataRow(component, "Enum_PatternType_Regex");
        FindDataRow(component, "Enum_TargetField_SeriesTitle");
    }

    [Fact]
    public async Task RenameTable_EditRow_CommitsFieldEditsAndFiresRulesChanged()
    {
        Library library = NewLibrary();
        LibraryRenameRule rule = NewRenameRule(
            library,
            "old-pattern",
            LibraryRenamePatternType.Contains,
            LibraryRenameTargetField.FileName,
            "old-replacement"
        );
        library.RenameRules = new ObservableCollection<LibraryRenameRule> { rule };

        int rulesChanged = 0;
        EventCallback rulesChangedCallback = EventCallback.Factory.Create(
            this,
            () => rulesChanged++
        );

        IRenderedComponent<EditLibraryRenames> component = RenderWithProviders<EditLibraryRenames>(
            parameters =>
            {
                parameters.Add(p => p.Library, library);
                parameters.Add(p => p.RulesChanged, rulesChangedCallback);
            }
        );

        await EnterEditAsync(component, FindDataRow(component, "old-pattern"));

        // EditLibraryRenameForm renders Pattern before Replacement, so the first text field is the
        // one to edit.
        IRenderedComponent<MudTextField<string>> patternField = component
            .FindComponents<MudTextField<string>>()
            .First();
        await component.InvokeAsync(() =>
            patternField.Instance.ValueChanged.InvokeAsync("new-pattern")
        );

        IRenderedComponent<MudSelect<LibraryRenameTargetField>> targetField =
            component.FindComponent<MudSelect<LibraryRenameTargetField>>();
        await component.InvokeAsync(() =>
            targetField.Instance.ValueChanged.InvokeAsync(LibraryRenameTargetField.ChapterTitle)
        );

        await CommitAsync(component);

        component.WaitForAssertion(() =>
        {
            Assert.Equal("new-pattern", rule.Pattern);
            Assert.Equal(LibraryRenameTargetField.ChapterTitle, rule.TargetField);
            FindDataRow(component, "new-pattern");
            FindDataRow(component, "Enum_TargetField_ChapterTitle");
        });

        // The contract of the rename form: every field edit notifies the owner so it can refresh
        // the preview.
        Assert.True(
            rulesChanged >= 2,
            $"Expected at least 2 RulesChanged invocations, got {rulesChanged}."
        );
    }

    [Fact]
    public async Task RenameTable_Delete_RemovesRowAndFiresRulesChanged()
    {
        Library library = NewLibrary();
        LibraryRenameRule rule = NewRenameRule(
            library,
            "seeded",
            LibraryRenamePatternType.Contains,
            LibraryRenameTargetField.FileName,
            "seeded-replacement"
        );
        library.RenameRules = new ObservableCollection<LibraryRenameRule> { rule };

        int rulesChanged = 0;
        EventCallback rulesChangedCallback = EventCallback.Factory.Create(
            this,
            () => rulesChanged++
        );

        IRenderedComponent<EditLibraryRenames> component = RenderWithProviders<EditLibraryRenames>(
            parameters =>
            {
                parameters.Add(p => p.Library, library);
                parameters.Add(p => p.RulesChanged, rulesChangedCallback);
            }
        );

        IElement row = FindDataRow(component, "seeded");
        IElement delete = row.QuerySelectorAll("button").Single();
        await component.InvokeAsync(() => delete.Click(new MouseEventArgs()));

        component.WaitForAssertion(() => Assert.Empty(DataRows(component)));
        Assert.Empty(library.RenameRules);
        Assert.Equal(1, rulesChanged);
    }

    // ------------------------------------------------------------------ helpers

    private static Library NewLibrary() => new() { Name = "Test Library" };

    private static LibraryFilterRule NewFilterRule(
        Library library,
        string pattern,
        LibraryFilterPatternType patternType,
        LibraryFilterTargetField targetField,
        FilterAction action
    ) =>
        new()
        {
            Pattern = pattern,
            PatternType = patternType,
            TargetField = targetField,
            Action = action,
            Library = library,
        };

    private static LibraryRenameRule NewRenameRule(
        Library library,
        string pattern,
        LibraryRenamePatternType patternType,
        LibraryRenameTargetField targetField,
        string replacement
    ) =>
        new()
        {
            Pattern = pattern,
            PatternType = patternType,
            TargetField = targetField,
            Replacement = replacement,
            Library = library,
        };

    private static IReadOnlyList<IElement> DataRows<TComponent>(
        IRenderedComponent<TComponent> component
    )
        where TComponent : class, IComponent =>
        component.FindAll("tbody tr").Where(tr => tr.QuerySelectorAll("td").Any()).ToList();

    private static IElement FindDataRow<TComponent>(
        IRenderedComponent<TComponent> component,
        string text
    )
        where TComponent : class, IComponent
    {
        IElement? row = component
            .FindAll("tbody tr")
            .FirstOrDefault(tr =>
                tr.QuerySelectorAll("td").Any()
                && tr.TextContent.Contains(text, StringComparison.Ordinal)
            );
        Assert.NotNull(row);
        return row!;
    }

    private static async Task ClickHeaderAddAsync<TComponent>(
        IRenderedComponent<TComponent> component
    )
        where TComponent : class, IComponent
    {
        IElement add = component.Find("thead button");
        await component.InvokeAsync(() => add.Click(new MouseEventArgs()));
    }

    private static async Task EnterEditAsync<TComponent>(
        IRenderedComponent<TComponent> component,
        IElement row
    )
        where TComponent : class, IComponent =>
        await component.InvokeAsync(() => row.Click(new MouseEventArgs()));

    private static async Task CommitAsync<TComponent>(IRenderedComponent<TComponent> component)
        where TComponent : class, IComponent
    {
        IElement commit = component.Find("button[aria-label='Commit edit']");
        await component.InvokeAsync(() => commit.Click(new MouseEventArgs()));
    }
}
