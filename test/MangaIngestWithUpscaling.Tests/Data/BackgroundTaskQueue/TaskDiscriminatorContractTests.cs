using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Data.BackgroundTaskQueue;

/// <summary>
/// Pins the contract between the persisted task JSON and the raw type/chapter lookups:
/// the <c>$type</c> discriminator must be the concrete type's name, because
/// <see cref="PersistedTaskQueries"/> matches on exactly that string. Renaming a task class (or
/// removing it from the registered set) would otherwise silently turn existing rows into no-ops.
/// </summary>
public class TaskDiscriminatorContractTests : IDisposable
{
    private static readonly Type[] ChapterScopedTaskTypes =
    {
        typeof(UpscaleTask),
        typeof(RepairUpscaleTask),
        typeof(RenameUpscaledChaptersSeriesTask),
        typeof(DetectSplitCandidatesTask),
        typeof(ApplySplitsTask),
    };

    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly ApplicationDbContext _db;

    public TaskDiscriminatorContractTests()
    {
        _testDb = TestDatabaseHelper.CreateInMemoryDatabase();
        _db = _testDb.Context;
    }

    public void Dispose() => _testDb.Dispose();

    public static IEnumerable<object[]> ConcreteTaskTypes() =>
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
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(ConcreteTaskTypes))]
    public void ConcreteTaskType_UsesItsTypeNameAsDiscriminator(Type taskType)
    {
        var task = (BaseTask)CreateInstance(taskType);

        // Serialize through the BaseTask static type, exactly like the EF value converter does, so
        // the polymorphic discriminator is emitted.
        var options = TaskJsonOptionsProvider.Options;
        string json = JsonSerializer.Serialize(task, options);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(
            document.RootElement.TryGetProperty("$type", out JsonElement discriminator),
            $"{taskType.Name} did not serialize a $type discriminator."
        );
        Assert.Equal(taskType.Name, discriminator.GetString());
    }

    [Fact]
    public async Task ChapterScopedTasks_AreFoundByTheRawQueryUsingTheirTypeName()
    {
        int order = 1;
        foreach (Type taskType in ChapterScopedTaskTypes)
        {
            object task = CreateInstance(taskType);
            taskType.GetProperty(nameof(DetectSplitCandidatesTask.ChapterId))!.SetValue(task, 7);
            _db.PersistedTasks.Add(
                new PersistedTask
                {
                    Data = (BaseTask)task,
                    Status = PersistedTaskStatus.Pending,
                    Order = order++,
                }
            );
        }

        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        List<PersistedTask> found = await PersistedTaskQueries
            .ForTaskTypesAndChapters(_db, [7], ChapterScopedTaskTypes.Select(t => t.Name).ToList())
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChapterScopedTaskTypes.Length, found.Count);

        IEnumerable<string> expected = ChapterScopedTaskTypes.Select(t => t.Name).OrderBy(n => n);
        IEnumerable<string> actual = found.Select(t => t.Data.GetType().Name).OrderBy(n => n);
        Assert.Equal(expected, actual);
    }

    private static object CreateInstance(Type type) =>
        type.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type)!
            : RuntimeHelpers.GetUninitializedObject(type);
}
