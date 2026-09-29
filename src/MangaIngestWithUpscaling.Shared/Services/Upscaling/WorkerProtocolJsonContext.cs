using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Source-generated JSON metadata for the NDJSON worker protocol, so serialization of job
/// requests, worker events, and control commands is reflection-free and works under NativeAOT.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(WorkerEvent))]
[JsonSerializable(typeof(WorkerJobRequest))]
[JsonSerializable(typeof(WorkerChapterRequest))]
[JsonSerializable(typeof(WorkerPageRequest))]
[JsonSerializable(typeof(WorkerCloseChapterRequest))]
[JsonSerializable(typeof(WorkerCommand))]
public partial class WorkerProtocolJsonContext : JsonSerializerContext { }
