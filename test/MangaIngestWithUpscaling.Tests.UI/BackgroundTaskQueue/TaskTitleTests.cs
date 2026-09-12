using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using NSubstitute;

namespace MangaIngestWithUpscaling.Tests.UI.BackgroundTaskQueue;

public class TaskTitleTests : BunitContext
{
    [Fact]
    public async Task ResolvesTitleOnlyOnce_WhenTaskReferenceIsUnchanged()
    {
        // Arrange
        var describer = Substitute.For<ITaskDescriber<BaseTask>>();
        describer.GetTitleAsync(Arg.Any<BaseTask>()).Returns(Task.FromResult("Resolved title"));

        var factory = Substitute.For<ITaskDescriberFactory>();
        factory.GetDescriber(Arg.Any<BaseTask>()).Returns(describer);
        Services.AddSingleton(factory);

        var task = new LoggingTask { Message = "hello" };

        // Act: render and force two additional render passes with the same parameter instance.
        var cut = Render<TaskTitle>(parameters => parameters.Add(p => p.Task, task));
        cut.WaitForAssertion(() => Assert.Contains("Resolved title", cut.Markup));
        cut.Render();
        cut.Render();

        // Assert: the (potentially database-backed) lookup ran exactly once.
        await describer.Received(1).GetTitleAsync(Arg.Any<BaseTask>());

        // A different task instance must trigger a fresh lookup.
        var other = new LoggingTask { Message = "world" };
        cut.Render(parameters => parameters.Add(p => p.Task, other));
        await describer.Received(1).GetTitleAsync(other);
    }
}
