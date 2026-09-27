using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Components.Pages;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LogModel;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Serilog.Events;

// The shared bUnit context aliases TestContext, so the xUnit-recommended
// TestContext.Current.CancellationToken is unavailable here.
#pragma warning disable xUnit1051

namespace MangaIngestWithUpscaling.Tests.UI.LogPages;

/// <summary>
/// Covers the logs page's per-operation <see cref="LoggingDbContext"/> usage and its paging reset on
/// a severity-filter change. The <c>Logs</c> table is excluded from EF migrations and normally
/// created by the Serilog sink, so the harness creates it explicitly.
/// </summary>
public class LogsTests : BunitContext
{
    private TestDatabaseHelper.TestDbContext _testDb = null!;
    private TestLoggingDbContextFactory _loggingFactory = null!;
    private IDbContextFactory<LoggingDbContext> _factorySpy = null!;

    public LogsTests()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _loggingFactory = new TestLoggingDbContextFactory(_testDb.Database);
        _factorySpy = Substitute.For<IDbContextFactory<LoggingDbContext>>();
        _factorySpy
            .CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _loggingFactory.CreateDbContext());
        CreateLogsTable();

        Services.AddMudServices();
        Services.AddSingleton(typeof(IStringLocalizer<>), typeof(MockStringLocalizer<>));
        Services.AddSingleton(_factorySpy);
        Services.AddSingleton(Substitute.For<IDialogService>());

        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("mudPopover.initialize").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.connect").SetVoidResult();
        JSInterop.SetupVoid("mudKeyInterceptor.updatekey").SetVoidResult();
        JSInterop.SetupVoid("mudScrollManager.lockScroll").SetVoidResult();
        JSInterop.SetupVoid("mudScrollListener.listenForScroll").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusFirst").SetVoidResult();
        JSInterop.SetupVoid("mudElementRef.focusLast").SetVoidResult();
        JSInterop.SetupVoid("localTime.formatElement").SetVoidResult();
    }

    [Fact]
    public async Task FilterChange_WhenNewTotalIsExactlyThePageEnd_ResetsToFirstPageInsteadOfRenderingBlank()
    {
        // The default filter is Warning, which also matches Error/Fatal. Seeding more Error rows
        // than the second page's start (page 1 * size 10 = 10) makes the "Error" filter total hit
        // exactly the end of page 1, which MudTable does not clamp (it only clamps strictly past).
        await SeedLogsAsync(CreateLogs(level: "Warning", count: 15, prefix: "warning"));
        await SeedLogsAsync(CreateLogs(level: "Error", count: 10, prefix: "error"));

        var cut = Render<Logs>();
        var table = cut.FindComponent<MudTable<Log>>();

        // Page 0 of the default 25-row result: the 10 newest rows are the error rows.
        cut.WaitForAssertion(() => Assert.Contains("error-01", cut.Markup));

        await cut.InvokeAsync(() => table.Instance.NavigateTo(1));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, table.Instance.CurrentPage);
            Assert.Contains("warning-15", cut.Markup);
        });

        // Switching to Error leaves exactly 10 rows. Without the reset the table would stay on page
        // 1 and ask for rows 10..19, rendering empty.
        var levelSelect = cut.FindComponent<MudSelect<LogEventLevel>>();
        await cut.InvokeAsync(() =>
            levelSelect.Instance.ValueChanged.InvokeAsync(LogEventLevel.Error)
        );

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(0, table.Instance.CurrentPage);
            Assert.Contains("error-01", cut.Markup);
        });
    }

    [Fact]
    public async Task ServerData_OpensAShortLivedContextPerRequest()
    {
        await SeedLogsAsync(CreateLogs(level: "Error", count: 1, prefix: "error"));
        _factorySpy.ClearReceivedCalls();

        var cut = Render<Logs>();
        cut.WaitForAssertion(() => Assert.Contains("error-01", cut.Markup));

        await _factorySpy.Received().CreateDbContextAsync(Arg.Any<CancellationToken>());
    }

    private void CreateLogsTable()
    {
        using var context = _loggingFactory.CreateDbContext();
        string ddl =
            _testDb.Database.Backend == TestDatabaseBackend.Postgres
                ? PostgresLogging.CreateTableSql
                : SqliteLogsDdl;
        context.Database.ExecuteSqlRaw(ddl);
    }

    private async Task SeedLogsAsync(IEnumerable<Log> logs)
    {
        await using var context = _loggingFactory.CreateDbContext();
        foreach (Log log in logs)
        {
            context.LogEntries.Add(log);
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static List<Log> CreateLogs(string level, int count, string prefix)
    {
        var logs = new List<Log>(count);
        for (int i = 1; i <= count; i++)
        {
            // Errors are newer than warnings so the combined (Warning+) view orders them first.
            var timestamp = new DateTime(
                2024,
                1,
                1,
                level == "Error" ? 1 : 0,
                0,
                0,
                DateTimeKind.Utc
            );
            logs.Add(
                new Log
                {
                    Timestamp = timestamp.AddMinutes(i),
                    Level = level,
                    RenderedMessage = $"{prefix}-{i:00}",
                }
            );
        }

        return logs;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Run teardown on the thread pool so neither bUnit's service-provider disposal nor the
        // database drop can resume on the renderer's synchronization context and deadlock.
        await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_testDb is not null)
        {
            TestDatabaseHelper.TestDbContext testDb = _testDb;
            _testDb = null!;
            await testDb.DisposeAsync().ConfigureAwait(false);
        }
    }

    private const string SqliteLogsDdl = """
        CREATE TABLE IF NOT EXISTS "Logs" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL,
            "Level" TEXT NOT NULL,
            "Exception" TEXT,
            "RenderedMessage" TEXT NOT NULL,
            "Properties" TEXT
        );
        """;

    private sealed class TestLoggingDbContextFactory(TestDatabase database)
        : IDbContextFactory<LoggingDbContext>
    {
        public LoggingDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<LoggingDbContext>();
            database.Configure(builder);
            return new LoggingDbContext(builder.Options);
        }
    }
}
