﻿using System.Threading.Channels;
using System;
using MangaIngestWithUpscaling.Data.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Data;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue;

public class UpscaleTaskProcessor : BackgroundService
{
    private readonly ChannelReader<PersistedTask> _reader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpscaleTaskProcessor> _logger;

    private readonly Lock _lock = new Lock();
    private CancellationTokenSource? currentStoppingToken;
    private PersistedTask? currentTask;

    public event Func<PersistedTask, Task>? StatusChanged;

    public UpscaleTaskProcessor(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<UpscaleTaskProcessor> logger)
    {
        _reader = taskQueue.UpscaleReader;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Cancels the current task if it matches the given task.
    /// The task is necessary to prevent canceling another if the task has already been processed.
    /// Otherwise, consistency issues may arise.
    /// </summary>
    /// <param name="checkAgainst">The task to check against if it is still the current task. Does so by using the Id.</param>
    public void CancelCurrent(PersistedTask checkAgainst)
    {
        using (_lock.EnterScope())
        {
            if (currentTask?.Id == checkAgainst.Id)
            {
                currentStoppingToken?.Cancel();
            }
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
                currentTask = task;
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
            StatusChanged?.Invoke(task);

            await task.Data.ProcessAsync(scope.ServiceProvider, stoppingToken);
            StatusChanged?.Invoke(task);

            task.Status = PersistedTaskStatus.Completed;
            task.ProcessedAt = DateTime.UtcNow;
            dbContext.Update(task);
            await dbContext.SaveChangesAsync();
            StatusChanged?.Invoke(task);
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
