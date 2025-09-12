using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpscalerProfileJsonDto))]
internal partial class UpscalerJsonContext : JsonSerializerContext
{
}