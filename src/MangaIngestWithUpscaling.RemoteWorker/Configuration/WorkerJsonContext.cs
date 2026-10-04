using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

[JsonSerializable(typeof(List<SplitFindingDto>))]
[JsonSerializable(typeof(SplitDetectionResult))]
[JsonSerializable(typeof(List<SplitDetectionResult>))]
[JsonSerializable(typeof(ImagePreprocessingOptions))]
public partial class WorkerJsonContext : JsonSerializerContext { }
