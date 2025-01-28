using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using ReactiveMarbles.ObservableEvents;

namespace MangaIngestWithUpscaling.Components.BackgroundTaskQueue
{
    public class TaskQueuesViewModel : ViewModelBase
    {
        private readonly TaskQueue _taskQueue;
        private readonly UpscaleTaskProcessor _upscaleTaskProcessor;
        private readonly StandardTaskProcessor _standardTaskProcessor;

        public TaskQueuesViewModel(
            TaskQueue taskQueue,
            UpscaleTaskProcessor upscaleTaskProcessor,
            StandardTaskProcessor standardTaskProcessor)
        {
            _taskQueue = taskQueue;
            _upscaleTaskProcessor = upscaleTaskProcessor;
            _standardTaskProcessor = standardTaskProcessor;

            //_taskQueue.Events().TaskEnqueued.Subscribe(_ => ProcessTasks());
        }

        private void ProcessTasks()
        {
        }
    }
}
