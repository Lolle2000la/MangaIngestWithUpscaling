using Serilog.Events;

namespace MangaIngestWithUpscaling.Services.Logging;

/// <summary>
/// Maps the logs UI's severity filter to the level names the Serilog sinks actually persist.
/// <para>
/// The sinks render <see cref="LogEventLevel"/> ("Verbose"…"Fatal"), whereas
/// <c>Microsoft.Extensions.Logging.LogLevel</c> uses different names ("Trace"…"Critical"). Deriving
/// the filter from the wrong enum silently hides <c>Verbose</c>/<c>Fatal</c> rows and offers a
/// <c>Critical</c> option that never matches, so both sides must use the same type.
/// </para>
/// </summary>
public static class LogLevelFilter
{
    /// <summary>
    /// The persisted level names at or above <paramref name="minimum" />, in ascending severity
    /// order. The result is a <see cref="List{T}"/> so EF Core can translate
    /// <c>Contains</c> into a SQL <c>IN</c> predicate.
    /// </summary>
    public static List<string> AtOrAbove(LogEventLevel minimum) =>
        Enum.GetNames<LogEventLevel>().Skip((int)minimum).ToList();
}
