using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.QuietHours;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Configuration;

/// <summary>
/// Pins that the worker's own <c>RegisterConfig</c> wires quiet hours: the gate, the validator and the
/// clock it reads. The gate is what stops the worker from claiming tasks, so a missing registration
/// would silently leave the worker running through its window.
/// </summary>
public class QuietHoursRegistrationTests
{
    /// <summary>Local time of day, the clock the gate reads.</summary>
    private static TimeSpan LocalNowOfDay() => TimeProvider.System.GetLocalNow().TimeOfDay;

    private static TimeSpan WrapAroundDay(TimeSpan value)
    {
        long ticksPerDay = TimeSpan.FromDays(1).Ticks;
        long ticks = value.Ticks % ticksPerDay;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + ticksPerDay : ticks);
    }

    /// <summary>
    /// Runs the real worker registration over an in-memory section, with no host started:
    /// <c>ValidateOnStart</c> is not what the worker relies on before its connection loop — resolving
    /// <see cref="IOptions{TOptions}" />.Value validates eagerly.
    /// </summary>
    private static ServiceProvider Register(string? start, string? end, bool enabled) =>
        RegisterSection(
            new Dictionary<string, string?>
            {
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)}"] = enabled
                    ? "true"
                    : "false",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Start)}"] = start,
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.End)}"] = end,
            }
        );

    private static ServiceProvider RegisterSection(Dictionary<string, string?> section)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(section);
        builder.RegisterConfig();
        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterConfig_RegistersAGateThatIsQuietInsideTheConfiguredWindow()
    {
        TimeSpan start = WrapAroundDay(LocalNowOfDay() - TimeSpan.FromHours(2));
        TimeSpan end = WrapAroundDay(LocalNowOfDay() + TimeSpan.FromHours(2));

        using ServiceProvider provider = Register(start.ToString(), end.ToString(), true);

        IQuietHoursGate gate = provider.GetRequiredService<IQuietHoursGate>();

        Assert.True(gate.Current.IsQuiet);
        Assert.NotNull(gate.Current.TimeUntilResume);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterConfig_BindsTheWindowAndProvidesTheClock()
    {
        using ServiceProvider provider = Register("22:00", "06:00", true);

        QuietHoursConfig config = provider.GetRequiredService<IOptions<QuietHoursConfig>>().Value;

        Assert.True(config.Enabled);
        Assert.Equal(TimeSpan.FromHours(22), config.Start);
        Assert.Equal(TimeSpan.FromHours(6), config.End);
        // The gate cannot read local time without a clock; the registration must supply it.
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterConfig_BindsTheWindowsFormThroughTheWorkersRegistration()
    {
        // The worker is the host that builds with the configuration source generator, so the general
        // form — a list of windows with day names, and Start/End left unset — has to be proven through
        // its real registration. A nullable TimeSpan that failed to bind would surface as 00:00 and
        // silently add a midnight-starting window here.
        using ServiceProvider provider = RegisterSection(
            new Dictionary<string, string?>
            {
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Enabled)}"] = "true",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.TimeZone)}"] = "UTC",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Windows)}:0:Start"] =
                    "22:00",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Windows)}:0:End"] = "06:00",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Windows)}:1:Start"] =
                    "12:00",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Windows)}:1:End"] = "13:00",
                [$"{QuietHoursConfig.Position}:{nameof(QuietHoursConfig.Windows)}:1:Days:0"] =
                    "Saturday",
            }
        );

        QuietHoursConfig config = provider.GetRequiredService<IOptions<QuietHoursConfig>>().Value;

        Assert.Null(config.Start);
        Assert.Null(config.End);
        Assert.Equal("UTC", config.TimeZone);
        Assert.Equal(2, config.Windows.Count);
        Assert.Equal(TimeSpan.FromHours(22), config.Windows[0].Start);
        QuietWindow weekend = config.Windows[1];
        Assert.Equal(DayOfWeek.Saturday, Assert.Single(weekend.Days));
        Assert.False(config.IsActive(DayOfWeek.Friday, new TimeSpan(12, 30, 0)));
        Assert.True(config.IsActive(DayOfWeek.Saturday, new TimeSpan(12, 30, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterConfig_RejectsAnOutOfRangeWindow()
    {
        // "25:00:00" is a legal TimeSpan but not a time of day. Without the validator the worker would
        // start, run through the intended window and never pause.
        using ServiceProvider provider = Register("25:00:00", "06:00", true);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<QuietHoursConfig>>().Value;
        });

        Assert.Contains("QuietHours:Start", exception.Message, StringComparison.Ordinal);
    }
}
