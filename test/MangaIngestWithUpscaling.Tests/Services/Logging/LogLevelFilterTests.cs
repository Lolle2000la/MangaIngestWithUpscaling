using MangaIngestWithUpscaling.Services.Logging;
using Serilog.Events;

namespace MangaIngestWithUpscaling.Tests.Services.Logging;

/// <summary>
/// Guards the logs page's severity filter against using the wrong level enum. The Serilog sinks
/// persist <see cref="LogEventLevel"/> names ("Verbose"…"Fatal"); the Web SDK's implicit
/// <c>Microsoft.Extensions.Logging.LogLevel</c> would yield "Trace"…"Critical" and never match
/// <c>Verbose</c>/<c>Fatal</c> rows.
/// </summary>
public class LogLevelFilterTests
{
    [Fact]
    public void AtOrAbove_ReturnsAscendingSeverityNames()
    {
        Assert.Equal(
            new[] { "Warning", "Error", "Fatal" },
            LogLevelFilter.AtOrAbove(LogEventLevel.Warning)
        );
        Assert.Equal(new[] { "Fatal" }, LogLevelFilter.AtOrAbove(LogEventLevel.Fatal));
    }

    [Fact]
    public void AtOrAbove_Verbose_ReturnsEveryPersistedSerilogLevelName()
    {
        List<string> all = LogLevelFilter.AtOrAbove(LogEventLevel.Verbose);

        Assert.Equal(Enum.GetNames<LogEventLevel>(), all);
        Assert.Contains("Verbose", all);
        Assert.Contains("Fatal", all);
        // Microsoft.Extensions.Logging's names would silently match nothing.
        Assert.DoesNotContain("Trace", all);
        Assert.DoesNotContain("Critical", all);
    }
}
