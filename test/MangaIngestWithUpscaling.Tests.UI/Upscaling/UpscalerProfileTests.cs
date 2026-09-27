using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.Upscaling;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Upscaling;

/// <summary>
/// Interaction tests for the upscaler-profile pages: the list's soft-delete, the create form's save
/// and the versioning edit form that deactivates the old profile and repoints the referencing
/// libraries and mangas at the new row.
/// </summary>
public class UpscalerProfileTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private ApplicationDbContext _dbContext = null!;
    private IDialogService _subDialogService = null!;

    public UpscalerProfileTests()
    {
        SetupDatabase();
        RegisterServices();
    }

    private void SetupDatabase()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;
    }

    private void RegisterServices()
    {
        _subDialogService = Substitute.For<IDialogService>();

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subDialogService);

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnFocusEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnFocusEvent").SetVoidResult();
        JSInterop.Setup<bool>("mudElementRef.focusFirst").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.focusLast").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.saveFocus").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.restoreFocus").SetResult(true);
    }

    private static UpscalerProfile NewProfile(string name) =>
        new()
        {
            Name = name,
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Webp,
            Quality = 80,
        };

    private async Task<UpscalerProfile> SeedProfileAsync(string name)
    {
        UpscalerProfile profile = NewProfile(name);
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return profile;
    }

    private void ConfigureMessageBox(bool? result)
    {
        _subDialogService
            .ShowMessageBoxAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DialogOptions>()
            )
            .Returns(Task.FromResult(result));
    }

    private static IElement? FindRow(IRenderedComponent<UpscalerProfiles> component, string name)
    {
        return component
            .FindAll("tr")
            .FirstOrDefault(tr => tr.QuerySelectorAll("td").Any() && tr.TextContent.Contains(name));
    }

    private async Task ClickRowDeleteAsync(
        IRenderedComponent<UpscalerProfiles> component,
        string name
    )
    {
        await component.InvokeAsync(() =>
        {
            IElement row = FindRow(component, name)!;
            row.QuerySelectorAll("button")
                .First(b => b.GetAttribute("title")?.Contains("Delete") == true)
                .Click(new MouseEventArgs());
        });
    }

    // ------------------------------------------------------------------ list + delete

    [Fact]
    public async Task UpscalerProfiles_DeleteConfirmed_SoftDeletesAndRemovesTheRow()
    {
        UpscalerProfile toDelete = await SeedProfileAsync("Profile One");
        await SeedProfileAsync("Profile Two");
        ConfigureMessageBox(true);

        IRenderedComponent<UpscalerProfiles> component = Render<UpscalerProfiles>();
        component.WaitForAssertion(() => Assert.NotNull(FindRow(component, "Profile One")));

        await ClickRowDeleteAsync(component, "Profile One");

        component.WaitForAssertion(() => Assert.Null(FindRow(component, "Profile One")));
        Assert.NotNull(FindRow(component, "Profile Two"));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.False(await verifyDb.UpscalerProfiles.AnyAsync(p => p.Id == toDelete.Id));
        Assert.True(
            await verifyDb
                .UpscalerProfiles.IgnoreQueryFilters()
                .AnyAsync(p => p.Id == toDelete.Id && p.Deleted)
        );
    }

    [Fact]
    public async Task UpscalerProfiles_DeleteCancelled_KeepsTheRowActive()
    {
        UpscalerProfile kept = await SeedProfileAsync("Profile One");
        ConfigureMessageBox(false);

        IRenderedComponent<UpscalerProfiles> component = Render<UpscalerProfiles>();
        component.WaitForAssertion(() => Assert.NotNull(FindRow(component, "Profile One")));

        await ClickRowDeleteAsync(component, "Profile One");

        // The reload after a cancelled delete must keep the profile visible.
        component.WaitForAssertion(() => Assert.NotNull(FindRow(component, "Profile One")));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        Assert.True(await verifyDb.UpscalerProfiles.AnyAsync(p => p.Id == kept.Id));
        Assert.False(
            await verifyDb
                .UpscalerProfiles.IgnoreQueryFilters()
                .Where(p => p.Id == kept.Id)
                .Select(p => p.Deleted)
                .FirstAsync()
        );
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task CreateUpscalerProfile_SavePersistsProfileAndNavigatesBack()
    {
        IRenderedComponent<CreateUpscalerProfile> component = Render<CreateUpscalerProfile>();

        IRenderedComponent<MudInput<string>> nameInput = component.FindComponent<
            MudInput<string>
        >();
        await component.InvokeAsync(() =>
            nameInput.Instance.ValueChanged.InvokeAsync("Brand New Profile")
        );

        IElement createButton = component
            .FindAll("button")
            .First(b => b.TextContent.Contains("Create", StringComparison.Ordinal));
        Assert.False(createButton.HasAttribute("disabled"));

        await component.InvokeAsync(() => createButton.Click(new MouseEventArgs()));

        await using var verifyDb = await _testDb.Database.CreateContextAsync();
        UpscalerProfile persisted = await verifyDb.UpscalerProfiles.SingleAsync();
        Assert.Equal("Brand New Profile", persisted.Name);

        var navigation = Services.GetRequiredService<NavigationManager>();
        component.WaitForAssertion(
            () => Assert.EndsWith("/upscaling/profiles", navigation.Uri),
            TimeSpan.FromSeconds(15)
        );
    }

    // ------------------------------------------------------------------ edit + versioning

    [Fact]
    public async Task EditUpscalerProfile_SaveCreatesNewVersionAndRepointsReferences()
    {
        var originalCreatedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        // Seed the old profile with a fixed creation date so preserving it is observable. The
        // automatic timestamp handling would otherwise stamp "now".
        _dbContext.SkipTimestampUpdates = true;
        UpscalerProfile oldProfile = NewProfile("Profile One");
        oldProfile.CreatedAt = originalCreatedAt;
        oldProfile.ModifiedAt = originalCreatedAt;
        _dbContext.UpscalerProfiles.Add(oldProfile);

        var library = new Library
        {
            Name = "Library A",
            NotUpscaledLibraryPath = "/test/library-a",
            UpscaledLibraryPath = "/test/library-a-upscaled",
            UpscalerProfile = oldProfile,
        };
        var manga = new Manga
        {
            PrimaryTitle = "Manga A",
            Library = library,
            UpscalerProfilePreference = oldProfile,
        };
        _dbContext.Libraries.Add(library);
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        _dbContext.SkipTimestampUpdates = false;

        int oldProfileId = oldProfile.Id;

        IRenderedComponent<EditUpscalerProfile> component = Render<EditUpscalerProfile>(
            parameters => parameters.Add(p => p.ProfileId, oldProfileId)
        );

        component.WaitForAssertion(() =>
        {
            MudInput<string>? input = component
                .FindComponents<MudInput<string>>()
                .Select(c => c.Instance)
                .FirstOrDefault(i => i.GetState(x => x.Value) == "Profile One");
            Assert.NotNull(input);
        });

        IRenderedComponent<MudInput<string>> nameInput = component
            .FindComponents<MudInput<string>>()
            .First(i => i.Instance.GetState(x => x.Value) == "Profile One");
        await component.InvokeAsync(() =>
            nameInput.Instance.ValueChanged.InvokeAsync("Profile One Renamed")
        );

        IElement saveButton = component
            .FindAll("button")
            .First(b => b.TextContent.Contains("Save", StringComparison.Ordinal));
        Assert.False(saveButton.HasAttribute("disabled"));
        await component.InvokeAsync(() => saveButton.Click(new MouseEventArgs()));

        var navigation = Services.GetRequiredService<NavigationManager>();
        // The save and reference repointing are async; wait for the redirect before asserting state.
        component.WaitForAssertion(
            () => Assert.EndsWith("/upscaling/profiles", navigation.Uri),
            TimeSpan.FromSeconds(15)
        );

        await using var verifyDb = await _testDb.Database.CreateContextAsync();

        UpscalerProfile newProfile = await verifyDb.UpscalerProfiles.SingleAsync(p =>
            p.Name == "Profile One Renamed"
        );
        Assert.NotEqual(oldProfileId, newProfile.Id);
        // The new version must keep the original creation date, not "now".
        Assert.Equal(originalCreatedAt, newProfile.CreatedAt);

        UpscalerProfile oldPersisted = await verifyDb
            .UpscalerProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.Id == oldProfileId);
        Assert.True(oldPersisted.Deleted);

        Library persistedLibrary = await verifyDb.Libraries.SingleAsync();
        Assert.Equal(newProfile.Id, persistedLibrary.UpscalerProfileId);

        Manga persistedManga = await verifyDb.MangaSeries.SingleAsync();
        Assert.Equal(newProfile.Id, persistedManga.UpscalerProfilePreferenceId);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Run the whole teardown on the thread pool: bUnit's service-provider disposal (which
        // disposes the shared ApplicationDbContext) and the database drop both resume async
        // continuations, and resuming them on the renderer's synchronization context can deadlock.
        await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
    }
}
