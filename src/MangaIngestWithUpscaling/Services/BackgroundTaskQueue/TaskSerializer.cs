using System.Text.Json;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public interface ITaskSerializer
{
    string Serialize(BaseTask task);
    BaseTask Deserialize(PersistedTask persistedTask);
    T Deserialize<T>(PersistedTask persistedTask)
        where T : BaseTask;
    JsonSerializerOptions Options { get; }
}

[RegisterSingleton]
public class TaskSerializer : ITaskSerializer
{
    public JsonSerializerOptions Options => TaskJsonOptionsProvider.Options;

    public string Serialize(BaseTask task) => JsonSerializer.Serialize(task, Options);

    public BaseTask Deserialize(PersistedTask persistedTask) => persistedTask.Data;

    public T Deserialize<T>(PersistedTask persistedTask)
        where T : BaseTask =>
        persistedTask.Data as T
        ?? throw new InvalidOperationException(
            $"Unable to deserialize persisted task payload as {typeof(T).Name}."
        );
}
