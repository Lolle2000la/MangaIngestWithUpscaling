using System.Runtime.CompilerServices;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Tests;

/// <summary>
/// Registers the production task types for polymorphic persistence in tests that build an
/// <c>ApplicationDbContext</c> directly (i.e. without going through the web host).
/// </summary>
internal static class TestPolymorphismSetup
{
    [ModuleInitializer]
    internal static void InitializeTaskPolymorphism()
    {
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);
    }
}
