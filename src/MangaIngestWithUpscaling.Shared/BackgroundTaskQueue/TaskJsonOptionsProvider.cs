using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;

public static class TaskJsonOptionsProvider
{
    private static readonly JsonPolymorphismOptions PolymorphismOptions = new()
    {
        TypeDiscriminatorPropertyName = "$type",
        IgnoreUnrecognizedTypeDiscriminators = true,
    };

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static void RegisterDerivedTypesFromAssemblies(params Assembly[] assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
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
