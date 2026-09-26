using System.Security.Claims;
using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Components;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using MudBlazor.Translations;
using ReactiveUI.Builder;
using Serilog;

// Configure the HTTP client factory to use HTTP/2 for unencrypted connections
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    // Enable detailed errors in development
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ConfigureEndpointDefaults(o =>
        {
            o.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
        });

        options.ListenAnyIP(8080);
        options.ListenAnyIP(
            8081,
            listenOptions =>
            {
                // fallback to allow HTTP/2 only, necessary for gRPC
                listenOptions.Protocols = HttpProtocols.Http2;
            }
        );
    });
}

builder.Configuration.AddEnvironmentVariables("Ingest_");

builder.RegisterConfig(); // Register the configuration classes

DatabaseConfiguration databaseConfiguration = DatabaseConfiguration.Resolve(builder.Configuration);

// Prepare the SQLite files (WAL, corruption handling) before Serilog opens logs.db, and make sure
// the PostgreSQL Logs table exists before the sink starts writing. Both run before Build().
DatabaseBootstrap.PrepareSqliteDatabases(databaseConfiguration);
DatabaseBootstrap.EnsurePostgresLogsTable(databaseConfiguration);

//Log.Logger = new LoggerConfiguration()
//    .ReadFrom.Configuration(builder.Configuration)
//    .CreateLogger();

builder.Services.AddSerilog(
    (services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
            );

        if (databaseConfiguration.IsSqlite)
        {
            lc.WriteTo.SQLite(
                databaseConfiguration.LogsDbPath,
                tableName: "Logs",
                // Persist UTC: SQLite reads the column back as DateTimeKind.Unspecified, which the UI
                // treats as UTC. The browser-side LocalTime component then converts the UTC instant
                // to the visitor's time zone. Storing local wall-clock here would be treated as UTC
                // and shown shifted (the pre-existing behavior this fixes). PostgreSQL's timestamptz
                // reads back as Utc, so both providers render the same instant.
                storeTimestampInUtc: true,
                retentionPeriod: TimeSpan.FromDays(7),
                maxDatabaseSize: 100,
                rollOver: false
            );
        }
        else
        {
            lc.WriteTo.PostgreSQL(
                databaseConfiguration.PostgresConnectionString,
                tableName: PostgresLogging.TableName,
                columnOptions: PostgresLogging.ColumnWriters,
                schemaName: PostgresLogging.SchemaName,
                needAutoCreateTable: false,
                retentionTime: TimeSpan.FromDays(7)
            );
        }
    }
);

// Configure Forwarded Headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // If the proxy isn't on localhost from the app container's perspective
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMemoryCache();

builder.Services.AddGrpc();
builder.Services.AddHealthChecks();

builder.Services.AddMudServices();
builder.Services.AddMudTranslations();
builder.Services.RegisterViewModels();
builder.Services.AddScoped<MangaJaNaiUpscaler>();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<
    AuthenticationStateProvider,
    IdentityRevalidatingAuthenticationStateProvider
>();

// Configure Authentication
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    if (builder.Configuration.GetValue<bool>("OIDC:Enabled"))
    {
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme; // "OIDC"
    }
});

authBuilder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null);
authBuilder.AddIdentityCookies();

// Conditionally add OIDC authentication
if (builder.Configuration.GetValue<bool>("OIDC:Enabled"))
{
    authBuilder.AddOpenIdConnect(
        OpenIdConnectDefaults.AuthenticationScheme,
        options => // Use the constant for scheme name, "OIDC"
        {
            builder.Configuration.GetSection("OIDC").Bind(options);
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.UseTokenLifetime = false;
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    // Ensure redirect URIs use HTTPS when behind a reverse proxy
                    if (
                        ctx.Request.Headers.ContainsKey("X-Forwarded-Proto")
                        && ctx.Request.Headers["X-Forwarded-Proto"].ToString().Contains("https")
                    )
                    {
                        ctx.ProtocolMessage.RedirectUri = ctx.ProtocolMessage.RedirectUri?.Replace(
                            "http://",
                            "https://"
                        );
                    }

                    return Task.CompletedTask;
                },
                OnTokenValidated = async ctx =>
                {
                    var signInManager = ctx.HttpContext.RequestServices.GetRequiredService<
                        SignInManager<ApplicationUser>
                    >();
                    var userManager = ctx.HttpContext.RequestServices.GetRequiredService<
                        UserManager<ApplicationUser>
                    >();

                    if (ctx.Principal == null)
                    {
                        ctx.Fail("Principal is null.");
                        return;
                    }

                    string? emailClaim =
                        ctx.Principal.FindFirstValue(ClaimTypes.Email)
                        ?? ctx.Principal.FindFirstValue("email");
                    string? nameIdentifier = ctx.Principal.FindFirstValue(
                        ClaimTypes.NameIdentifier
                    );

                    if (emailClaim != null && nameIdentifier != null)
                    {
                        ApplicationUser? user = await userManager.FindByEmailAsync(emailClaim);
                        if (user == null)
                        {
                            user = new ApplicationUser
                            {
                                UserName = emailClaim,
                                Email = emailClaim,
                                EmailConfirmed = true,
                            };
                            IdentityResult createUserResult = await userManager.CreateAsync(user);
                            if (!createUserResult.Succeeded)
                            {
                                ctx.Fail(
                                    $"Failed to create user: {string.Join(", ", createUserResult.Errors.Select(e => e.Description))}"
                                );
                                return;
                            }
                        }

                        var externalLoginInfo = new UserLoginInfo(
                            ctx.Scheme.Name,
                            nameIdentifier,
                            ctx.Scheme.Name
                        );
                        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);
                        if (
                            !logins.Any(l =>
                                l.LoginProvider == externalLoginInfo.LoginProvider
                                && l.ProviderKey == externalLoginInfo.ProviderKey
                            )
                        )
                        {
                            IdentityResult addLoginResult = await userManager.AddLoginAsync(
                                user,
                                externalLoginInfo
                            );
                            if (!addLoginResult.Succeeded)
                            {
                                ctx.Fail(
                                    $"Failed to add OIDC login to user: {string.Join(", ", addLoginResult.Errors.Select(e => e.Description))}"
                                );
                                return;
                            }
                        }

                        await signInManager.SignInAsync(user, false);
                    }
                    else
                    {
                        ctx.Fail("Email or NameIdentifier claim not found.");
                        return;
                    }
                },
            };
        }
    );
}

// Register a factory to create short-lived DbContext instances for parallel/background operations
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    DatabaseSetup.UseDatabaseProvider(
        options,
        databaseConfiguration.Provider,
        databaseConfiguration.ApplicationConnectionString,
        databaseConfiguration.ApplicationMigrationsAssembly
    )
);
builder.Services.AddDbContext<ApplicationDbContext>(
    options =>
        DatabaseSetup.UseDatabaseProvider(
            options,
            databaseConfiguration.Provider,
            databaseConfiguration.ApplicationConnectionString,
            databaseConfiguration.ApplicationMigrationsAssembly
        ),
    optionsLifetime: ServiceLifetime.Singleton
);
builder.Services.AddDbContext<LoggingDbContext>(options =>
    DatabaseSetup.UseDatabaseProvider(
        options,
        databaseConfiguration.Provider,
        databaseConfiguration.IsSqlite
            ? databaseConfiguration.LoggingConnectionReadOnlyString!
            : databaseConfiguration.PostgresConnectionString,
        databaseConfiguration.ApplicationMigrationsAssembly
    )
);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder
    .Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;
        options.SignIn.RequireConfirmedPhoneNumber = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<CustomUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

builder
    .Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>()
    .SetApplicationName("manga-ingest-with-upscaling");

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.AddAuthorization(options =>
{
    if (builder.Configuration.GetValue<bool>("OIDC:Enabled"))
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
});

// Add the services used by the app
builder.Services.RegisterAppServices();

var rxApp = RxAppBuilder.CreateReactiveUIBuilder().WithBlazor().BuildApp();

if (rxApp.MainThreadScheduler != null)
{
    builder.Services.AddSingleton(rxApp.MainThreadScheduler);
}
if (rxApp.TaskpoolScheduler != null)
{
    builder.Services.AddSingleton(rxApp.TaskpoolScheduler);
}

var app = builder.Build();

app.UseForwardedHeaders();

// Warn users who are running a deprecated image variant
if (app.Configuration.GetValue<bool>("DeprecatedImageVariant"))
{
    var deprecationLogger = app.Services.GetRequiredService<ILogger<Program>>();
    deprecationLogger.LogWarning(
        "DEPRECATION WARNING: You are using a deprecated Docker image variant. "
            + "These images are deprecated and will be removed in a future release. "
            + "Please switch to the standard image (ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest) "
            + "and apply your desired configuration via environment variables. "
            + "For GPU backend selection set Ingest_Upscaler__PreferredGpuBackend (e.g. CUDA, CUDA_12_8, ROCm, ROCm_GFX120X, XPU). "
            + "For remote-only mode set Ingest_Upscaler__RemoteOnly=true. "
            + "See docs/GPU_BACKEND_CONFIGURATION.md and docs/REMOTE_ONLY_VARIANT.md for full migration details."
    );
}

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await DatabaseBootstrap.ApplyStartupMigrationsAsync(
        scope.ServiceProvider,
        databaseConfiguration,
        app.Environment,
        logger
    );

    // Also initialize python environment
    var upscalerConfig = scope.ServiceProvider.GetRequiredService<IOptions<UpscalerConfig>>();
    if (upscalerConfig.Value.RemoteOnly)
    {
        logger.LogInformation(
            "Upscaler is configured to run only on the remote worker, skipping local environment preparation."
        );
    }
    else
    {
        var pythonService = scope.ServiceProvider.GetRequiredService<IPythonService>();
        if (!pythonService.IsPythonInstalled())
        {
            logger.LogError(
                "Python is not installed on the system. Please install Python 3.6 or newer and ensure it is available on the system PATH."
            );
        }
        else
        {
            logger.LogInformation("Python is installed on the system.");

            Directory.CreateDirectory(upscalerConfig.Value.PythonEnvironmentDirectory);

            PythonEnvironment environment = await pythonService.PreparePythonEnvironment(
                upscalerConfig.Value.PythonEnvironmentDirectory,
                upscalerConfig.Value.PreferredGpuBackend,
                upscalerConfig.Value.ForceAcceptExistingEnvironment
            );
            PythonService.Environment = environment;

            logger.LogInformation(
                $"Python environment prepared at {environment.PythonExecutablePath} with {environment.InstalledBackend} backend"
            );
        }

        // Download the models once at startup instead of on every usage
        var mangaJaNaiUpscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();
        await mangaJaNaiUpscaler.DownloadModelsIfNecessary(CancellationToken.None);
    }
}

app.UseAuthentication();
app.UseAuthorization();

var supportedCultures = new[] { "en-US", "de-DE", "ja-JP" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

localizationOptions.RequestCultureProviders.Insert(
    0,
    new CustomRequestCultureProvider(async context =>
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var localeClaim = user.FindFirst("locale");
            if (localeClaim != null && !string.IsNullOrEmpty(localeClaim.Value))
            {
                return new ProviderCultureResult(localeClaim.Value);
            }
        }
        return await Task.FromResult<ProviderCultureResult?>(null);
    })
);

app.UseRequestLocalization(localizationOptions);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// For self hosted apps https redirection doesn't work all too well, so we disable it
//app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapApiEndpoints();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
// Only map these if OIDC is NOT enabled, or map them conditionally
if (!app.Configuration.GetValue<bool>("OIDC:Enabled"))
{
    app.MapAdditionalIdentityEndpoints();
}

// Add OIDC Logout Endpoint if OIDC is enabled
if (app.Configuration.GetValue<bool>("OIDC:Enabled"))
{
    app.MapPost(
            "/Account/LogoutOidc",
            async (
                HttpContext context,
                SignInManager<ApplicationUser> signInManager,
                string? returnUrl
            ) =>
            {
                await signInManager.SignOutAsync();
                await context.SignOutAsync(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    new AuthenticationProperties { RedirectUri = returnUrl ?? "/" }
                );
            }
        )
        .RequireAuthorization();
}

app.Run();
