using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using System.Threading.Channels;
using System;
using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue
{
    public interface ITaskQueue
    {
        Task EnqueueAsync<T>(T taskData) where T : BaseTask;
    }

    public class TaskQueue : ITaskQueue, IHostedService
    {
        private readonly Channel<PersistedTask> _standardChannel;
        private readonly Channel<PersistedTask> _upscaleChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TaskQueue> _logger;

        public ChannelReader<PersistedTask> StandardReader => _standardChannel.Reader;
        public ChannelReader<PersistedTask> UpscaleReader => _upscaleChannel.Reader;

        public TaskQueue(IServiceScopeFactory scopeFactory, ILogger<TaskQueue> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _standardChannel = Channel.CreateUnbounded<PersistedTask>();
            _upscaleChannel = Channel.CreateUnbounded<PersistedTask>();
        }

        public async Task EnqueueAsync<T>(T taskData) where T : BaseTask
        {
            var taskItem = new PersistedTask { Data = taskData };

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await dbContext.PersistedTasks.AddAsync(taskItem);
            await dbContext.SaveChangesAsync();

            var channel = taskData is UpscaleTask ? _upscaleChannel : _standardChannel;
            await channel.Writer.WriteAsync(taskItem);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var pendingTasks = await dbContext.PersistedTasks
                .Where(t => t.Status == PersistedTaskStatus.Pending)
                .ToListAsync();

            foreach (var task in pendingTasks)
            {
                var channel = task.Data is UpscaleTask ? _upscaleChannel : _standardChannel;
                await channel.Writer.WriteAsync(task);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
