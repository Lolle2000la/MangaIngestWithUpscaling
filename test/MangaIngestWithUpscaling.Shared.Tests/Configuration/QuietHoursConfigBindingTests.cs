using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// Pins the binding of the <c>QuietHours</c> section through the exact registration both hosts use.
/// <para>
/// The RemoteWorker (and the app) build with <c>EnableConfigurationBindingGenerator</c>, so these tests
/// exist to prove the generated binder handles the shapes the docs promise — in particular
/// <see cref="TimeSpan" />? boundaries, which no other setting in the repo exercises: a nullable value
/// type that the binder cannot produce would otherwise surface as <see cref="TimeSpan.Zero" /> (a
/// window starting at midnight) or as a startup exception.
/// </para>
/// </summary>
public class QuietHoursConfigBindingTests
{
    private static async Task<QuietHoursConfig> ResolveFromJsonAsync(
        string quietHoursJson,
        CancellationToken cancellationToken
    )
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(
                new MemoryStream(Encoding.UTF8.GetBytes($$"""{"QuietHours":{{quietHoursJson}}}"""))
            )
            .Build();

        return await ResolveAsync(configuration, cancellationToken);
    }

    private static async Task<QuietHoursConfig> ResolveAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken
    )
    {
        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // The same registration Main and the RemoteWorker make.
                services
                    .AddOptions<QuietHoursConfig>()
                    .Bind(configuration.GetSection(QuietHoursConfig.Position))
                    .ValidateOnStart();
                services.AddSingleton<
                    IValidateOptions<QuietHoursConfig>,
                    QuietHoursConfigValidator
                >();
            })
            .Build();

        await host.StartAsync(cancellationToken);
        QuietHoursConfig resolved = host
            .Services.GetRequiredService<IOptions<QuietHoursConfig>>()
            .Value;
        await host.StopAsync(cancellationToken);
        return resolved;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SimpleForm_BindsTheDailyPairAsStrings()
    {
        QuietHoursConfig resolved = await ResolveFromJsonAsync(
            """{"Enabled":true,"Start":"22:00","End":"06:00"}""",
            TestContext.Current.CancellationToken
        );

        Assert.True(resolved.Enabled);
        Assert.Equal(TimeSpan.FromHours(22), resolved.Start);
        Assert.Equal(TimeSpan.FromHours(6), resolved.End);
        Assert.Empty(resolved.Windows);
        // The bound window must actually behave as configured, not just compare equal.
        Assert.True(resolved.IsActive(DayOfWeek.Monday, new TimeSpan(23, 30, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GeneralForm_BindsWindowsAndDaysAsStrings()
    {
        QuietHoursConfig resolved = await ResolveFromJsonAsync(
            """
            {"Enabled":true,"TimeZone":"Europe/Berlin","Windows":[
              {"Start":"22:00","End":"06:00"},
              {"Start":"12:00","End":"13:00","Days":["Saturday","Sunday"]}
            ]}
            """,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("Europe/Berlin", resolved.TimeZone);
        Assert.Null(resolved.Start);
        Assert.Equal(2, resolved.Windows.Count);
        Assert.Equal(TimeSpan.FromHours(22), resolved.Windows[0].Start);
        Assert.Equal(TimeSpan.FromHours(6), resolved.Windows[0].End);
        Assert.Empty(resolved.Windows[0].Days);
        Assert.Equal(
            new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
            resolved.Windows[1].Days.ToArray()
        );
        // Day names must bind to the enum, not silently stay empty (which would mean "every day").
        Assert.False(resolved.IsActive(DayOfWeek.Friday, new TimeSpan(12, 30, 0)));
        Assert.True(resolved.IsActive(DayOfWeek.Saturday, new TimeSpan(12, 30, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AbsentStartAndEnd_BindAsNullNotZero()
    {
        // A binder that cannot handle TimeSpan? would leave the property at its default, and the
        // default of a non-nullable TimeSpan is 00:00 — a window that starts at midnight, silently.
        QuietHoursConfig resolved = await ResolveFromJsonAsync(
            """{"Enabled":false,"Start":null,"End":null}""",
            TestContext.Current.CancellationToken
        );

        Assert.Null(resolved.Start);
        Assert.Null(resolved.End);

        // And the same for a section that omits the keys entirely.
        QuietHoursConfig absent = await ResolveFromJsonAsync(
            """{"Enabled":true,"Windows":[{"Start":"22:00","End":"06:00"}]}""",
            TestContext.Current.CancellationToken
        );

        Assert.Null(absent.Start);
        Assert.Null(absent.End);
        QuietWindow window = Assert.Single(absent.Windows);
        Assert.Equal(TimeSpan.FromHours(22), window.Start);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnvironmentVariable_BindsThroughTheIngestPrefix()
    {
        // Both hosts add the "Ingest_" environment source, so a container deployment configures the
        // schedule with Ingest_QuietHours__Start rather than a file. Both halves of the pair are set
        // because one alone is rejected by the validator (and would fail the host start here).
        const string startVariable = "Ingest_QuietHours__Start";
        const string endVariable = "Ingest_QuietHours__End";
        string? previousStart = Environment.GetEnvironmentVariable(startVariable);
        string? previousEnd = Environment.GetEnvironmentVariable(endVariable);
        Environment.SetEnvironmentVariable(startVariable, "23:30");
        Environment.SetEnvironmentVariable(endVariable, "02:00");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables("Ingest_")
                .Build();

            QuietHoursConfig resolved = await ResolveAsync(
                configuration,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(new TimeSpan(23, 30, 0), resolved.Start);
            Assert.Equal(TimeSpan.FromHours(2), resolved.End);
        }
        finally
        {
            Environment.SetEnvironmentVariable(startVariable, previousStart);
            Environment.SetEnvironmentVariable(endVariable, previousEnd);
        }
    }
}
