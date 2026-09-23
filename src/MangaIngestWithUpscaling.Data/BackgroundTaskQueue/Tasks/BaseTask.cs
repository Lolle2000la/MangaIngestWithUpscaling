using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Represents a task that can be processed by the background task queue.
/// Concrete types live in the web application and are registered at runtime through
/// <c>TaskJsonOptionsProvider</c>, so no <see cref="JsonDerivedTypeAttribute"/> list is declared
/// here.
/// </summary>
public class BaseTask
{
    public virtual int RetryFor { get; set; } = 0;

    [NotMapped]
    [JsonIgnore]
    public ProgressInfo Progress { get; } = new();

    public virtual Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
