using ReactiveUI.Builder;

namespace MangaIngestWithUpscaling.Tests.UI;

/// <summary>
/// Initializes ReactiveUI exactly once per test process. Components such as
/// <c>FolderPicker</c> inherit from ReactiveUI's injectable component base and need the
/// application host configured, but multiple test classes must not initialize it twice.
/// </summary>
internal static class ReactiveUiTestSetup
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            RxAppBuilder.CreateReactiveUIBuilder().WithBlazor().BuildApp();
            _initialized = true;
        }
    }
}
