using System.Threading.Channels;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue;

public class StandardTaskProcessor(
    TaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<StandardTaskProcessor> logger,
    ITaskPersistenceService taskPersistenceService
) : BackgroundTaskProcessorBase(taskQueue, scopeFactory, logger, taskPersistenceService)
{
    private ChannelReader<object> Reader => TaskQueue.StandardReader;

    protected override string ProcessingFailedLogMessage => "Error processing task {TaskId}";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Reader.ReadAsync(stoppingToken);
            var task = TaskQueue.DequeueStandard();

            if (task == null)
            {
                continue;
            }

            CancellationTokenSource taskStoppingToken = BeginCurrentTask(task, stoppingToken);

            try
            {
                await ProcessTaskAsync(task, taskStoppingToken.Token);
            }
            finally
            {
                DisposeCurrentStoppingToken(taskStoppingToken);
            }
        }
    }

    protected override Task<bool> TryAcquireTaskAsync(
        PersistedTask task,
        CancellationToken stoppingToken
    ) => ClaimAsync(task, stoppingToken);
}
