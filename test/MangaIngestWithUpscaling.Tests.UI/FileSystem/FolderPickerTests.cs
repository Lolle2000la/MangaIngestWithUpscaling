using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MangaIngestWithUpscaling.Components.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.FileSystem;

/// <summary>
/// Interaction tests for the folder picker. A real <see cref="FolderPickerViewModel"/> and real
/// temp directories are used; the view model throttles directory loads, so the tests drive it
/// directly and pump renders with <c>WaitForAssertion</c>.
/// </summary>
public class FolderPickerTests : BunitContext
{
    private readonly string _rootPath;

    static FolderPickerTests()
    {
        ReactiveUiTestSetup.EnsureInitialized();
    }

    public FolderPickerTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "folder-picker-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_rootPath);

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        // The app registers the view model transient; each picker owns its own instance.
        Services.AddTransient<FolderPickerViewModel>();

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

    private string CreateTempDir(string relative)
    {
        string path = Path.Combine(_rootPath, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private IRenderedComponent<FolderPicker> RenderPicker(
        string root,
        string? selectedPath = null,
        EventCallback<string?> selectedPathChanged = default
    )
    {
        Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
        });

        return Render<FolderPicker>(parameters =>
        {
            parameters.Add(p => p.RootDirectory, root);
            parameters.Add(p => p.Title, "Pick a folder");
            parameters.Add(p => p.SelectedPath, selectedPath);
            parameters.Add(p => p.SelectedPathChanged, selectedPathChanged);
        });
    }

    [Fact]
    public async Task ClearSelection_InvokesChangedWithNullAndEmptiesField()
    {
        string root = CreateTempDir("root");
        string selected = CreateTempDir("root/selected");

        var changed = new List<string?>();
        var component = RenderPicker(
            root,
            selected,
            EventCallback.Factory.Create<string?>(this, changed.Add)
        );

        // OnParametersSet pushes the selected path into the view model, which round-trips it back
        // through SelectedPathChanged.
        component.WaitForAssertion(() => Assert.Contains(selected, changed));

        changed.Clear();
        IElement clearButton = component
            .FindAll("button")
            .First(b => b.GetAttribute("title") == "Clear selection");
        await component.InvokeAsync(() => clearButton.Click(new MouseEventArgs()));

        component.WaitForAssertion(() =>
        {
            Assert.Equal(new string?[] { null }, changed);
            IRenderedComponent<MudInputString> selectedField = component
                .FindComponents<MudInputString>()
                .Single(i => i.Instance.ReadOnly);
            Assert.True(string.IsNullOrEmpty(selectedField.Instance.GetState(x => x.Value)));
        });
    }

    [Fact]
    public async Task NavigatingThenReRenderingWithSameRoot_DoesNotResetViewModelsDirectory()
    {
        string root = CreateTempDir("root");
        string child = CreateTempDir("root/child");
        string grandChild = CreateTempDir("root/child/grandchild");

        var component = RenderPicker(root);
        FolderPickerViewModel viewModel = component.Instance.ViewModel!;

        // Wait for the initial root to be applied and its children loaded.
        component.WaitForAssertion(
            () => Assert.Contains(viewModel.TreeItems, item => item.Value?.Path == child),
            TimeSpan.FromSeconds(10)
        );

        // The user navigates into the child directory (double-click / GoToParent).
        await component.InvokeAsync(() => viewModel.RootDirectory = child);
        component.WaitForAssertion(
            () => Assert.Contains(viewModel.TreeItems, item => item.Value?.Path == grandChild),
            TimeSpan.FromSeconds(10)
        );

        // A parent re-render (every input change in EditLibraryForm) passes the same
        // RootDirectory; it must not snap the view model back to the initial root.
        component.Render(parameters =>
        {
            parameters.Add(p => p.RootDirectory, root);
            parameters.Add(p => p.Title, "Pick a folder");
            parameters.Add(p => p.SelectedPath, (string?)null);
            parameters.Add(p => p.SelectedPathChanged, default(EventCallback<string?>));
        });

        Assert.Equal(child, viewModel.RootDirectory);
        Assert.Contains(viewModel.TreeItems, item => item.Value?.Path == grandChild);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Run the teardown on the thread pool so bUnit's provider disposal and the temp-dir
        // cleanup cannot post continuations back onto the renderer's synchronization context.
        await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);
        TryDeleteDirectory(_rootPath);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
