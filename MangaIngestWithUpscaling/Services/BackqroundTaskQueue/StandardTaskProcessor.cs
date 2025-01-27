using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using System;
using System.Threading.Channels;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue
{
    public class StandardTaskProcessor : BackgroundService
    {
        private readonly ChannelReader<PersistedTask> _reader;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StandardTaskProcessor> _logger;

        private Lock _lock = new Lock();
        private CancellationTokenSource currentStoppingToken;

        public StandardTaskProcessor(
            TaskQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<StandardTaskProcessor> logger)
        {
            _reader = taskQueue.StandardReader;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public void CancelCurrent()
        {
            using (_lock.EnterScope())
            {
                currentStoppingToken?.Cancel();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var task = await _reader.ReadAsync(stoppingToken);
                using (_lock.EnterScope())
                {
                    currentStoppingToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                }
                await ProcessTaskAsync(task, currentStoppingToken.Token);
            }
        }

        protected async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
        {

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                task.Status = PersistedTaskStatus.Processing;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();

                // Polymorphic processing based on concrete type
                await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);

                task.Status = PersistedTaskStatus.Completed;
                task.ProcessedAt = DateTime.UtcNow;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing task {TaskId}", task.Id);
                task.Status = PersistedTaskStatus.Failed;
                task.RetryCount++;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();
            }
        }
    }
}
