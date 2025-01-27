using System.Threading.Channels;
using System;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue
{
    public class UpscaleTaskProcessor : BackgroundService
    {
        private readonly ChannelReader<PersistedTask> _reader;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UpscaleTaskProcessor> _logger;

        private Lock _lock = new Lock();
        private CancellationTokenSource currentStoppingToken;

        public UpscaleTaskProcessor(
            TaskQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<UpscaleTaskProcessor> logger)
        {
            _reader = taskQueue.UpscaleReader;
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

        private async Task ProcessTaskAsync(PersistedTask task, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                task.Status = PersistedTaskStatus.Processing;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();

                await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);

                task.Status = PersistedTaskStatus.Completed;
                task.ProcessedAt = DateTime.UtcNow;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upscale task {TaskId} failed", task.Id);
                task.Status = PersistedTaskStatus.Failed;
                task.RetryCount++;
                dbContext.Update(task);
                await dbContext.SaveChangesAsync();
            }
        }
    }
}
