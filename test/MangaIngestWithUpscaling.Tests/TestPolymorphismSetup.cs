using System.Runtime.CompilerServices;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Tests;

internal static class TestPolymorphismSetup
{
    [ModuleInitializer]
    internal static void InitializeTaskPolymorphism()
    {
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);
    }
}
