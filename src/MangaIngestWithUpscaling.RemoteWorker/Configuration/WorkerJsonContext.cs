using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;

namespace MangaIngestWithUpscaling.RemoteWorker.Configuration;

[JsonSerializable(typeof(ImagePreprocessingOptions))]
public partial class WorkerJsonContext : JsonSerializerContext { }
