using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

/// <summary>
/// Provides the <see cref="JsonSerializerOptions"/> used to persist polymorphic
/// <see cref="BaseTask"/> payloads. The concrete task types live in the web application, so they
/// cannot be referenced from this assembly; instead they are registered from the assembly that
/// defines them (see <see cref="RegisterDerivedTypesFromAssemblies"/>), which the application does
/// once at startup.
/// </summary>
public static class TaskJsonOptionsProvider
{
    private static readonly object Sync = new();

    private static readonly JsonPolymorphismOptions PolymorphismOptions = new()
    {
        TypeDiscriminatorPropertyName = "$type",
        IgnoreUnrecognizedTypeDiscriminators = true,
    };

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Registers every concrete <see cref="BaseTask"/> implementation found in the given
    /// assemblies. Safe to call multiple times and from multiple threads.
    /// </summary>
    public static void RegisterDerivedTypesFromAssemblies(params Assembly[] assemblies)
    {
        lock (Sync)
        {
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }

                foreach (Type type in types)
                {
                    if (type == typeof(BaseTask))
                    {
                        continue;
                    }

                    if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                    {
                        continue;
                    }

                    if (!typeof(BaseTask).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (PolymorphismOptions.DerivedTypes.Any(dt => dt.DerivedType == type))
                    {
                        continue;
                    }

                    PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
                }
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(BaseTask))
            {
                typeInfo.PolymorphismOptions = PolymorphismOptions;
            }
        });

        return new JsonSerializerOptions
        {
            WriteIndented = false,
            AllowTrailingCommas = true,
            TypeInfoResolver = resolver,
        };
    }
}
