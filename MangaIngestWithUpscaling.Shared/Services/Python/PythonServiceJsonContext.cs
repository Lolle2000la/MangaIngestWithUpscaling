using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Services.Python;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EnvironmentState))]
internal partial class PythonServiceJsonContext : JsonSerializerContext
{
}