namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class LoggingTask : BaseTask
{
    public override string TaskFriendlyName => $"Logging Task: {Message}";
    public required string Message { get; set; }

    public override int RetryFor { get; set; } = 1;

    public override async Task ProcessAsync(
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        var logger = services.GetRequiredService<ILogger<LoggingTask>>();
        logger.LogInformation(Message);
        await Task.Delay(1000, cancellationToken);
        await Task.CompletedTask;
    }
}
