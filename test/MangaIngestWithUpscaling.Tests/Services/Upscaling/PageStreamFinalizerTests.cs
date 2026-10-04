using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Upscaling;

public class PageStreamFinalizerTests
{
    private static readonly PageStreamFinalizeTexts Texts = PageStreamFinalizeTexts.UpscaleAssembly;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheSessionIsNotComplete_ReturnsNotComplete()
    {
        var spool = Substitute.For<IPageSpoolStore>();
        spool.IsComplete(Arg.Any<PageStreamSession>()).Returns(false);
        PageStreamSession session = Session();
        bool finalized = false;

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () =>
                {
                    finalized = true;
                    return Task.CompletedTask;
                },
                _ => { },
                (_, _) => Task.CompletedTask,
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.NotComplete, outcome.Status);
        Assert.False(finalized);
        spool.DidNotReceive().TryBeginAssembly(Arg.Any<PageStreamSession>(), Arg.Any<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheFinalizeSucceeds_ReturnsFinalizedSuccessfully()
    {
        var spool = Complete();
        PageStreamSession session = Session();
        var dropped = new List<PageStreamSession>();

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () => Task.CompletedTask,
                dropped.Add,
                (_, _) => Task.CompletedTask,
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.FinalizedSuccessfully, outcome.Status);
        Assert.Contains(session, dropped);
        spool.Received(1).EndAssembly(session);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheFinalizeMustRestartWithAReset_DropsTheSpool()
    {
        var spool = Complete();
        PageStreamSession session = Session();
        var dropped = new List<PageStreamSession>();

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () => throw new PageStreamRestartException("source changed", resetSpool: true),
                dropped.Add,
                (_, _) => Task.CompletedTask,
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.Restart, outcome.Status);
        Assert.True(outcome.ResetSpool);
        Assert.Contains(session, dropped);
        spool.DidNotReceive().ForgetMissingPages(Arg.Any<PageStreamSession>());
        spool.Received(1).EndAssembly(session);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheFinalizeMustRestartWithoutAReset_KeepsTheSpool()
    {
        var spool = Complete();
        PageStreamSession session = Session();
        var dropped = new List<PageStreamSession>();

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () => throw new PageStreamRestartException("a spooled page is missing"),
                dropped.Add,
                (_, _) => Task.CompletedTask,
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.Restart, outcome.Status);
        Assert.False(outcome.ResetSpool);
        Assert.Empty(dropped);
        spool.Received(1).ForgetMissingPages(session);
        spool.Received(1).EndAssembly(session);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheFinalizeHitsAnIoFailure_ReturnsTransientFailure()
    {
        var spool = Complete();
        PageStreamSession session = Session();
        var dropped = new List<PageStreamSession>();
        var failures = new List<(int TaskId, string Message)>();

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () => throw new IOException("disk full"),
                dropped.Add,
                (taskId, message) =>
                {
                    failures.Add((taskId, message));
                    return Task.CompletedTask;
                },
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.TransientFailure, outcome.Status);
        Assert.False(outcome.ResetSpool);
        Assert.Empty(dropped);
        spool.DidNotReceive().ForgetMissingPages(Arg.Any<PageStreamSession>());
        Assert.Empty(failures);
        spool.Received(1).EndAssembly(session);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeAsync_WhenTheFinalizeFailsGenerically_MarksTheTaskFailed()
    {
        var spool = Complete();
        PageStreamSession session = Session();
        var failures = new List<(int TaskId, string Message)>();

        PageStreamFinalizeOutcome outcome = await Finalizer(spool)
            .FinalizeAsync(
                session,
                session.Identity,
                () => throw new InvalidOperationException("boom"),
                _ => { },
                (taskId, message) =>
                {
                    failures.Add((taskId, message));
                    return Task.CompletedTask;
                },
                Texts
            );

        Assert.Equal(PageStreamFinalizeStatus.TerminalFailure, outcome.Status);
        (int taskId, string message) = Assert.Single(failures);
        Assert.Equal(session.TaskId, taskId);
        Assert.Equal("Assembling the chapter failed: boom", message);
        spool.Received(1).EndAssembly(session);
    }

    private static PageStreamFinalizer Finalizer(IPageSpoolStore spool) =>
        new(spool, NullLogger<PageStreamFinalizer>.Instance);

    private static IPageSpoolStore Complete()
    {
        var spool = Substitute.For<IPageSpoolStore>();
        spool.IsComplete(Arg.Any<PageStreamSession>()).Returns(true);
        spool.TryBeginAssembly(Arg.Any<PageStreamSession>(), Arg.Any<string>()).Returns(true);
        return spool;
    }

    private static PageStreamSession Session() =>
        new(
            taskId: 7,
            identity: "content",
            engineIdentity: "engine",
            pageCount: 1,
            directory: "/nonexistent/spool"
        );
}
