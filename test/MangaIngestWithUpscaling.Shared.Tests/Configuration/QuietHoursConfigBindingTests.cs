using System.Text;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

/// <summary>
/// Pins the binding of the <c>QuietHours</c> section through the exact registration both hosts use.
/// The boundaries are <see cref="TimeSpan" />s so the worker's configuration source generator can
/// bind them; a string like <c>"22:00"</c> must therefore reach the options as 22 hours.
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
    public async Task Json_BindsTheWindowThroughTheHostsRegistration()
    {
        QuietHoursConfig resolved = await ResolveFromJsonAsync(
            """{"Enabled":true,"Start":"22:00","End":"06:00"}""",
            TestContext.Current.CancellationToken
        );

        Assert.True(resolved.Enabled);
        Assert.Equal(TimeSpan.FromHours(22), resolved.Start);
        Assert.Equal(TimeSpan.FromHours(6), resolved.End);
        // The bound window must actually behave as configured, not just compare equal.
        Assert.True(resolved.IsActive(new TimeSpan(23, 30, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AbsentSection_LeavesTheFeatureDisabled()
    {
        // No QuietHours section at all has to be the exact "behave as today" state.
        QuietHoursConfig resolved = await ResolveFromJsonAsync(
            """{}""",
            TestContext.Current.CancellationToken
        );

        Assert.False(resolved.Enabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnvironmentVariable_BindsThroughTheIngestPrefix()
    {
        // Both hosts add the "Ingest_" environment source, so a container deployment configures the
        // window with Ingest_QuietHours__Start rather than a file.
        const string variable = "Ingest_QuietHours__Start";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "23:30");
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
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
