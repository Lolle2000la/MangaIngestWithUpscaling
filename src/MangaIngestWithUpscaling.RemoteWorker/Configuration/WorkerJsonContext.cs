using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

[JsonSerializable(typeof(List<SplitFindingDto>))]
[JsonSerializable(typeof(SplitDetectionResult))]
[JsonSerializable(typeof(List<SplitDetectionResult>))]
public partial class WorkerJsonContext : JsonSerializerContext { }
