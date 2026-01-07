using AutoRegisterInject;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;

[RegisterScoped]
public class TaskDescriberFactory(IServiceProvider services) : ITaskDescriberFactory
{
    public ITaskDescriber<BaseTask> GetDescriber(BaseTask task)
    {
        return task switch
        {
            LoggingTask => services.GetRequiredService<LoggingTaskDescriber>(),
            UpscaleTask => services.GetRequiredService<UpscaleTaskDescriber>(),
            RepairUpscaleTask => services.GetRequiredService<RepairUpscaleTaskDescriber>(),
            ScanIngestTask => services.GetRequiredService<ScanIngestTaskDescriber>(),
            RenameUpscaledChaptersSeriesTask =>
                services.GetRequiredService<RenameUpscaledChaptersSeriesTaskDescriber>(),
            LibraryIntegrityCheckTask =>
                services.GetRequiredService<LibraryIntegrityCheckTaskDescriber>(),
            MergeMangaTask => services.GetRequiredService<MergeMangaTaskDescriber>(),
            ApplyImageFiltersTask => services.GetRequiredService<ApplyImageFiltersTaskDescriber>(),
            UpdatePerceptualHashesTask =>
                services.GetRequiredService<UpdatePerceptualHashesTaskDescriber>(),
            DetectSplitCandidatesTask =>
                services.GetRequiredService<DetectSplitCandidatesTaskDescriber>(),
            ApplySplitsTask => services.GetRequiredService<ApplySplitsTaskDescriber>(),
            _ => throw new NotImplementedException(
                $"No describer for task type {task.GetType().Name}"
            ),
        };
    }
}
