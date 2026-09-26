using System.Runtime.CompilerServices;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling;

/// <summary>
/// Registers the concrete background task types for polymorphic JSON persistence as soon as the
/// application assembly is loaded. The task types live in this assembly, so the data layer cannot
/// discover them on its own.
/// </summary>
internal static class TaskPolymorphismSetup
{
    [ModuleInitializer]
    internal static void InitializeTaskPolymorphism()
    {
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(UpscaleTask).Assembly);
    }
}
