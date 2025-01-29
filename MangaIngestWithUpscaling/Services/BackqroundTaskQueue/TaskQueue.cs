using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using System.Threading.Channels;
using System;
using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

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

    public event Func<PersistedTask, Task>? TaskEnqueued;

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

        TaskEnqueued?.Invoke(taskItem);

        // cleanup old tasks
        var oldTasks = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Completed)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(100)
            .ToListAsync();

        if (oldTasks.Count > 25)
        {
            _logger.LogInformation("Cleaning up {TaskCount} old tasks.", oldTasks.Count);
        }

        dbContext.PersistedTasks.RemoveRange(oldTasks);
        await dbContext.SaveChangesAsync();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pendingTasks = await dbContext.PersistedTasks
            .Where(t => t.Status == PersistedTaskStatus.Pending || t.Status == PersistedTaskStatus.Processing)
            .ToListAsync(cancellationToken: cancellationToken);

        // make processing tasks pending again
        foreach (var task in pendingTasks)
        {
            if (task.Status == PersistedTaskStatus.Processing)
            {
                task.Status = PersistedTaskStatus.Pending;
                dbContext.Update(task);
            }
        }

        _logger.LogInformation("Enqueuing {TaskCount} pending tasks from last run.", pendingTasks.Count);

        foreach (var task in pendingTasks)
        {
            var channel = task.Data is UpscaleTask ? _upscaleChannel : _standardChannel;
            await channel.Writer.WriteAsync(task, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
