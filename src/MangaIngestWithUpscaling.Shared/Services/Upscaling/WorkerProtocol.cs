using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Shared JSON options for the NDJSON worker protocol: snake_case naming and case-insensitive
/// matching. Unknown properties are ignored by default, so the worker can add fields without
/// breaking the client.
/// </summary>
public static class WorkerJson
{
    // Source-generated (WorkerProtocolJsonContext) so the NDJSON worker protocol serializes
    // under NativeAOT — the remote worker is published with PublishAot=true. The naming/case
    // settings and the reflection-free resolver all come from the context's own options.
    public static readonly JsonSerializerOptions Options = WorkerProtocolJsonContext
        .Default
        .Options;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerReadyEvent), "ready")]
[JsonDerivedType(typeof(WorkerAcceptedEvent), "accepted")]
[JsonDerivedType(typeof(WorkerRejectedEvent), "rejected")]
[JsonDerivedType(typeof(WorkerStartedEvent), "started")]
[JsonDerivedType(typeof(WorkerProgressEvent), "progress")]
[JsonDerivedType(typeof(WorkerDoneEvent), "done")]
[JsonDerivedType(typeof(WorkerErrorEvent), "error")]
[JsonDerivedType(typeof(WorkerCancelledEvent), "cancelled")]
[JsonDerivedType(typeof(WorkerCacheReleasedEvent), "cache_released")]
[JsonDerivedType(typeof(WorkerPongEvent), "pong")]
[JsonDerivedType(typeof(WorkerExitedEvent), "exited")]
public abstract record WorkerEvent;

public sealed record WorkerReadyEvent(int Capacity, WorkerDeviceInfo? Device) : WorkerEvent;

public sealed record WorkerAcceptedEvent(string? Id, int Capacity) : WorkerEvent;

public sealed record WorkerRejectedEvent(string? Id, string? Reason) : WorkerEvent;

public sealed record WorkerStartedEvent(string? Id) : WorkerEvent;

public sealed record WorkerProgressEvent(
    string? Id,
    int? Completed,
    int? ArchiveTotal,
    int? ArchiveCompleted,
    string? Phase
) : WorkerEvent;

public sealed record WorkerDoneEvent(
    string? Id,
    string? Status,
    double ElapsedSeconds,
    List<WorkerDoneFile>? Files
) : WorkerEvent;

public sealed record WorkerErrorEvent(string? Id, string? Message) : WorkerEvent;

public sealed record WorkerCancelledEvent(string? Id) : WorkerEvent;

/// <summary>
/// Reply to a <c>release_cache</c> command. <c>Status</c> is <c>ok</c> when cached VRAM was
/// returned to the driver, or <c>busy</c> when a job was in flight and the release was skipped.
/// </summary>
public sealed record WorkerCacheReleasedEvent(string? Status) : WorkerEvent;

public sealed record WorkerPongEvent() : WorkerEvent;

public sealed record WorkerExitedEvent() : WorkerEvent;

public sealed record WorkerDoneFile(string? Input, string? Output, string? Status);

public sealed record WorkerDeviceInfo(
    int? SelectedDeviceIndex,
    bool UseCpu,
    bool UseFp16,
    string? ModelsDirectory
);

public sealed record WorkerJobRequest
{
    public string Type { get; init; } = "job";
    public required string Id { get; init; }
    public required WorkerJobInput Input { get; init; }
    public required WorkerJobOutput Output { get; init; }
    public WorkerJobOptions? Options { get; init; }
}

public sealed record WorkerJobInput
{
    public required string Path { get; init; }
}

public sealed record WorkerJobOutput
{
    public required string Folder { get; init; }
    public required string Filename { get; init; }
    public required string Format { get; init; }
    public bool Overwrite { get; init; }
}

public sealed record WorkerJobOptions
{
    public int Scale { get; init; }
}

/// <summary>
/// A client-to-worker control message (e.g. shutdown, cancel). Typed so it serializes without
/// reflection under NativeAOT, unlike a <c>Dictionary&lt;string, object?&gt;</c> payload.
/// </summary>
public sealed record WorkerCommand(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null
);
