using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Represents a task that can be processed by the background task queue.
/// Note that you must use the <see cref="JsonDerivedTypeAttribute"/> to specify the concrete types,
/// otherwise the JSON serializer will not be able to deserialize the polymorphic types.
/// </summary>
[JsonDerivedType(typeof(LoggingTask), nameof(LoggingTask))]
[JsonDerivedType(typeof(UpscaleTask), nameof(UpscaleTask))]
[JsonDerivedType(typeof(RepairUpscaleTask), nameof(RepairUpscaleTask))]
[JsonDerivedType(typeof(ScanIngestTask), nameof(ScanIngestTask))]
[JsonDerivedType(
    typeof(RenameUpscaledChaptersSeriesTask),
    nameof(RenameUpscaledChaptersSeriesTask)
)]
[JsonDerivedType(typeof(LibraryIntegrityCheckTask), nameof(LibraryIntegrityCheckTask))]
[JsonDerivedType(typeof(MergeMangaTask), nameof(MergeMangaTask))]
[JsonDerivedType(typeof(ApplyImageFiltersTask), nameof(ApplyImageFiltersTask))]
[JsonDerivedType(typeof(UpdatePerceptualHashesTask), nameof(UpdatePerceptualHashesTask))]
[JsonDerivedType(typeof(DetectSplitCandidatesTask), nameof(DetectSplitCandidatesTask))]
[JsonDerivedType(typeof(ApplySplitsTask), nameof(ApplySplitsTask))]
public class BaseTask
{
    public virtual int RetryFor { get; set; } = 0;

    [NotMapped]
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public ProgressInfo Progress { get; } = new();

    public virtual Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
