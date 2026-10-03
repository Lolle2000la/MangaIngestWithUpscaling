using System.Text.Json.Serialization;
using MangaIngestWithUpscaling.Shared.Data.Analysis;

namespace MangaIngestWithUpscaling.Shared.Services.Analysis;

/// <summary>
/// NDJSON protocol of <c>detect_server.py</c>. Mirrors the shape used by the upscaler's
/// <c>worker.py</c> client: one JSON request per line on stdin, one JSON event per line on stdout.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DetectServerReadyEvent), "ready")]
[JsonDerivedType(typeof(DetectServerResultEvent), "result")]
[JsonDerivedType(typeof(DetectServerErrorEvent), "error")]
[JsonDerivedType(typeof(DetectServerCancelledEvent), "cancelled")]
[JsonDerivedType(typeof(DetectServerCacheReleasedEvent), "cache_released")]
[JsonDerivedType(typeof(DetectServerPongEvent), "pong")]
[JsonDerivedType(typeof(DetectServerExitedEvent), "exited")]
public abstract record DetectServerEvent;

public sealed record DetectServerReadyEvent(string? Device, int? TargetWidth) : DetectServerEvent;

public sealed record DetectServerResultEvent(string? Id, SplitDetectionResult? Result)
    : DetectServerEvent;

public sealed record DetectServerErrorEvent(string? Id, string? Message) : DetectServerEvent;

public sealed record DetectServerCancelledEvent(string? Id) : DetectServerEvent;

public sealed record DetectServerCacheReleasedEvent(string? Status) : DetectServerEvent;

public sealed record DetectServerPongEvent : DetectServerEvent;

public sealed record DetectServerExitedEvent : DetectServerEvent;

/// <summary>A <c>detect</c> request. Exactly one of <see cref="Path"/> / <see cref="Data"/> is set.</summary>
public sealed record DetectServerRequest
{
    public string Type { get; init; } = "detect";
    public required string Id { get; init; }
    public string? Path { get; init; }

    /// <summary>Base64-encoded image bytes; used instead of <see cref="Path"/> for in-memory pages.</summary>
    public string? Data { get; init; }
    public string? Name { get; init; }
}

/// <summary>A control request (<c>cancel</c>, <c>release_cache</c>, <c>ping</c>, <c>shutdown</c>).</summary>
public sealed record DetectServerCommand(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null
);

/// <summary>
/// Source-generated JSON metadata for the detection server protocol, so it works under NativeAOT.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(DetectServerEvent))]
[JsonSerializable(typeof(DetectServerRequest))]
[JsonSerializable(typeof(DetectServerCommand))]
public partial class DetectServerJsonContext : JsonSerializerContext { }
