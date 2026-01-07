using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

public interface ITaskDescriberFactory
{
    ITaskDescriber<BaseTask> GetDescriber(BaseTask task);
}
