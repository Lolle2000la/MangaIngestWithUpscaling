using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Shared.Configuration;

[JsonSerializable(typeof(SplitDetectionResult))]
public partial class SharedJsonContext : JsonSerializerContext { }
