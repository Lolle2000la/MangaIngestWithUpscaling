using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Integrations;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using TestContext = Xunit.TestContext;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

[Collection(TestDatabaseCollection.Name)]
public class SplitProcessingCoordinatorTests
{
    private readonly TestDatabaseFixture _fixture;

    public SplitProcessingCoordinatorTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<TestDatabaseBackend> Backends => TestDatabaseBackends.Enabled;

    private sealed class CoordinatorTestScope : IAsyncDisposable
    {
        public CoordinatorTestScope(
            TestDatabase database,
            ApplicationDbContext context,
            SplitProcessingCoordinator coordinator,
            ITaskQueue taskQueue,
            IChapterChangedNotifier chapterChangedNotifier
        )
        {
            Database = database;
            Context = context;
            Coordinator = coordinator;
            TaskQueue = taskQueue;
            ChapterChangedNotifier = chapterChangedNotifier;
        }

        public TestDatabase Database { get; }

        public ApplicationDbContext Context { get; }

        public SplitProcessingCoordinator Coordinator { get; }

        public ITaskQueue TaskQueue { get; }

        public IChapterChangedNotifier ChapterChangedNotifier { get; }

        public ValueTask DisposeAsync()
        {
            Context.Dispose();
            return Database.DisposeAsync();
        }
    }

    private async Task<CoordinatorTestScope> CreateScopeAsync(
        TestDatabaseBackend backend,
        CancellationToken cancellationToken
    )
    {
        var database = await _fixture.CreateDatabaseAsync(backend, cancellationToken);
        var context = await database.CreateContextAsync(cancellationToken);

        var taskQueue = Substitute.For<ITaskQueue>();
        var chapterChangedNotifier = Substitute.For<IChapterChangedNotifier>();
        var fileSystem = Substitute.For<IFileSystem>();
        var logger = Substitute.For<ILogger<SplitProcessingCoordinator>>();
        var coordinator = new SplitProcessingCoordinator(
            context,
            taskQueue,
            chapterChangedNotifier,
            fileSystem,
            logger
        );

        return new CoordinatorTestScope(
            database,
            context,
            coordinator,
            taskQueue,
            chapterChangedNotifier
        );
    }

    private static async Task<Chapter> CreateChapterAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken
    )
    {
        var library = new Library { Name = "Test Lib" };
        context.Libraries.Add(library);
        await context.SaveChangesAsync(cancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        context.MangaSeries.Add(manga);
        await context.SaveChangesAsync(cancellationToken);

        var chapter = new Chapter
        {
            FileName = "ch1.zip",
            MangaId = manga.Id,
            Manga = manga,
        };
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync(cancellationToken);

        return chapter;
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ShouldProcessAsync_ReturnsTrue_WhenNoStateExists(TestDatabaseBackend backend)
    {
        await using var scope = await CreateScopeAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        var chapterId = 1;

        var result = await scope.Coordinator.ShouldProcessAsync(
            chapterId,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(result);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ShouldProcessAsync_ReturnsTrue_WhenVersionIsOld(TestDatabaseBackend backend)
    {
        await using var scope = await CreateScopeAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        var chapter = await CreateChapterAsync(
            scope.Context,
            TestContext.Current.CancellationToken
        );
        scope.Context.ChapterSplitProcessingStates.Add(
            new ChapterSplitProcessingState
            {
                ChapterId = chapter.Id,
                LastProcessedDetectorVersion = SplitDetectionService.CURRENT_DETECTOR_VERSION - 1,
                ModifiedAt = DateTime.UtcNow,
            }
        );
        await scope.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await scope.Coordinator.ShouldProcessAsync(
            chapter.Id,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(result);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ShouldProcessAsync_ReturnsFalse_WhenVersionIsCurrent(
        TestDatabaseBackend backend
    )
    {
        await using var scope = await CreateScopeAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        var chapter = await CreateChapterAsync(
            scope.Context,
            TestContext.Current.CancellationToken
        );
        scope.Context.ChapterSplitProcessingStates.Add(
            new ChapterSplitProcessingState
            {
                ChapterId = chapter.Id,
                LastProcessedDetectorVersion = SplitDetectionService.CURRENT_DETECTOR_VERSION,
                ModifiedAt = DateTime.UtcNow,
            }
        );
        await scope.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await scope.Coordinator.ShouldProcessAsync(
            chapter.Id,
            StripDetectionMode.DetectOnly,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task EnqueueDetectionAsync_EnqueuesTask(TestDatabaseBackend backend)
    {
        await using var scope = await CreateScopeAsync(
            backend,
            TestContext.Current.CancellationToken
        );
        var chapterId = 1;

        await scope.Coordinator.EnqueueDetectionAsync(
            chapterId,
            TestContext.Current.CancellationToken
        );

        await scope
            .TaskQueue.Received(1)
            .EnqueueAsync(
                Arg.Is<DetectSplitCandidatesTask>(t =>
                    t.ChapterId == chapterId
                    && t.DetectorVersion == SplitDetectionService.CURRENT_DETECTOR_VERSION
                )
            );
    }
}
