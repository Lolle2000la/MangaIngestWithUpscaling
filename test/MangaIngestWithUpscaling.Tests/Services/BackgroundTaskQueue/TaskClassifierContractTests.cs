using System.Runtime.CompilerServices;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

namespace MangaIngestWithUpscaling.Tests.Services.BackgroundTaskQueue;

/// <summary>
/// Pins the explicit classification contract in <see cref="TaskClassifier" />: the name lists are
/// derived from one set of types and every concrete chapter-scoped task type is classified
/// deliberately (upscale family or <see cref="ApplySplitsTask" />), so adding a task type cannot
/// silently leave it routed or guarded by accident.
/// </summary>
public class TaskClassifierContractTests
{
    private static IEnumerable<Type> ConcreteTaskTypes =>
        // The concrete task types live in the web application assembly, not in the data assembly.
        typeof(UpscaleTask)
            .Assembly.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && !t.IsInterface
                && !t.IsGenericTypeDefinition
                && t != typeof(BaseTask)
                && typeof(BaseTask).IsAssignableFrom(t)
            )
            .OrderBy(t => t.Name);

    [Fact]
    [Trait("Category", "Unit")]
    public void ClassificationLists_AreNonEmptyAndHaveNoDuplicates()
    {
        Assert.NotEmpty(TaskClassifier.UpscaleFamilyTaskTypeNames);
        Assert.NotEmpty(TaskClassifier.ChapterScopedTaskTypeNames);
        Assert.Equal(
            TaskClassifier.UpscaleFamilyTaskTypeNames.Count,
            TaskClassifier.UpscaleFamilyTaskTypeNames.Distinct().Count()
        );
        Assert.Equal(
            TaskClassifier.ChapterScopedTaskTypeNames.Count,
            TaskClassifier.ChapterScopedTaskTypeNames.Distinct().Count()
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChapterScopedTaskTypeNames_AreTheUpscaleFamilyPlusApplySplits()
    {
        HashSet<string> expected =
        [
            .. TaskClassifier.UpscaleFamilyTaskTypeNames,
            nameof(ApplySplitsTask),
        ];

        Assert.Equal(expected, TaskClassifier.ChapterScopedTaskTypeNames.ToHashSet());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryChapterScopedTaskType_IsClassifiedExplicitly()
    {
        List<Type> chapterScoped = ConcreteTaskTypes
            .Where(typeof(IChapterTask).IsAssignableFrom)
            .ToList();

        // Guard against the reflection finding nothing (a moved assembly would otherwise make the
        // contract vacuous).
        Assert.NotEmpty(chapterScoped);

        foreach (Type taskType in chapterScoped)
        {
            Assert.Contains(taskType.Name, TaskClassifier.ChapterScopedTaskTypeNames);
            Assert.True(
                TaskClassifier.UpscaleFamilyTaskTypeNames.Contains(taskType.Name)
                    || taskType == typeof(ApplySplitsTask),
                $"{taskType.Name} is chapter-scoped but is neither in the upscale family nor ApplySplitsTask."
            );
        }

        // And the chapter-scoped list must not name a task that does not exist or is not
        // chapter-scoped.
        Assert.Equal(
            chapterScoped.Select(t => t.Name).OrderBy(n => n),
            TaskClassifier.ChapterScopedTaskTypeNames.OrderBy(n => n)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsUpscaleTask_AgreesWithTheUpscaleFamilyNames()
    {
        List<Type> upscaleFamily = ConcreteTaskTypes
            .Where(t => TaskClassifier.UpscaleFamilyTaskTypeNames.Contains(t.Name))
            .ToList();
        Assert.Equal(TaskClassifier.UpscaleFamilyTaskTypeNames.Count, upscaleFamily.Count);

        foreach (Type taskType in ConcreteTaskTypes)
        {
            var instance = (BaseTask)CreateInstance(taskType);
            bool expected = TaskClassifier.UpscaleFamilyTaskTypeNames.Contains(taskType.Name);
            Assert.Equal(expected, TaskClassifier.IsUpscaleTask(instance));
        }
    }

    private static object CreateInstance(Type type) =>
        type.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type)!
            : RuntimeHelpers.GetUninitializedObject(type);
}
