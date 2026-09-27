using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bunit.Rendering;
using MangaIngestWithUpscaling.Components.FileSystem;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.ImageFiltering;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.Libraries;

/// <summary>
/// Shared harness for the Libraries component interaction tests: an isolated database, temp
/// directories, the component DI graph and a few rendering helpers.
/// </summary>
public abstract class LibraryTestBase : BunitContext
{
    protected TestDatabaseHelper.TestDbContext _testDb = null!;
    protected ApplicationDbContext _dbContext = null!;
    protected ISnackbar _subSnackbar = null!;
    protected IImageFilterService _subImageFilterService = null!;
    protected string _rootPath = null!;

    protected LibraryTestBase()
    {
        ReactiveUiTestSetup.EnsureInitialized();

        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "manga-library-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_rootPath);

        _subSnackbar = Substitute.For<ISnackbar>();
        _subImageFilterService = Substitute.For<IImageFilterService>();

        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;

        RegisterServices();
    }

    protected virtual void RegisterServices()
    {
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_dbContext);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            new TestDbContextFactory(_testDb.Database)
        );
        Services.AddSingleton(_subSnackbar);
        Services.AddSingleton(_subImageFilterService);
        Services.AddSingleton(Options.Create(new KavitaConfiguration()));

        // Each FolderPicker owns its view model (the app registers it transient). A single shared
        // instance would make every picker react to every other picker's selected path.
        Services.AddTransient(_ => Substitute.For<FolderPickerViewModel>());

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.saveFocus").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.restoreFocus").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnBlurEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.addOnFocusEvent").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.removeOnFocusEvent").SetVoidResult();
        JSInterop.Setup<bool>("mudElementRef.focusFirst").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.focusLast").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.saveFocus").SetResult(true);
        JSInterop.Setup<bool>("mudElementRef.restoreFocus").SetResult(true);
    }

    protected string CreateTempDir(string name)
    {
        string path = Path.Combine(_rootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    protected string CreateTempFile(string name, string content = "x")
    {
        string path = Path.Combine(_rootPath, name);
        File.WriteAllText(path, content);
        return path;
    }

    protected IRenderedComponent<T> RenderWithProviders<T>(
        Action<ComponentParameterCollectionBuilder<T>>? parameterBuilder = null
    )
        where T : class, IComponent
    {
        RenderDialogHost();
        return parameterBuilder != null ? Render<T>(parameterBuilder) : Render<T>();
    }

    /// <summary>
    /// Renders just the MudBlazor providers so dialogs can be opened through the real
    /// <see cref="IDialogService"/> and discovered in the render tree.
    /// </summary>
    protected IRenderedComponent<ContainerFragment> RenderDialogHost()
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });
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

        TryDeleteDirectory(_rootPath);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
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

/// <summary>
/// Minimal <see cref="IBrowserFile"/> double whose stream is an in-memory copy of the given bytes.
/// </summary>
internal sealed class FakeBrowserFile : IBrowserFile
{
    private readonly byte[] _content;

    public FakeBrowserFile(string name, byte[] content)
    {
        Name = name;
        _content = content;
    }

    public string Name { get; }
    public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
    public long Size => _content.Length;
    public string ContentType => "image/png";

    public Stream OpenReadStream(
        long maxAllowedSize = 512000,
        CancellationToken cancellationToken = default
    )
    {
        if (_content.Length > maxAllowedSize)
        {
            throw new IOException("The file is larger than the maximum allowed size.");
        }

        return new MemoryStream(_content, writable: false);
    }
}
