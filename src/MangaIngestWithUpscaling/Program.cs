using System.Data.SQLite;
using System.Security.Claims;
using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Components;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services;
using MangaIngestWithUpscaling.Services.ChapterMerging;
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

string connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

SQLiteConnectionStringBuilder sqliteConnectionStringBuilder = new(connectionString);

var loggingConnectionString =
    builder.Configuration.GetConnectionString("LoggingConnection") ?? "Data Source=logs.db";

var loggingConnectionReadOnlyStringBuilder = new SQLiteConnectionStringBuilder(
    loggingConnectionString
);
var loggingConnectionReadOnlyString = loggingConnectionReadOnlyStringBuilder.ConnectionString;

//Log.Logger = new LoggerConfiguration()
//    .ReadFrom.Configuration(builder.Configuration)
//    .CreateLogger();

builder.Services.AddSerilog(
    (services, lc) =>
        lc
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.SQLite(
                Path.GetFullPath(loggingConnectionReadOnlyStringBuilder.DataSource),
                tableName: "Logs",
                retentionPeriod: TimeSpan.FromDays(7)
            )
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

builder.Services.AddGrpc();

builder.Services.AddMudServices();
builder.Services.AddMudTranslations();
builder.Services.RegisterViewModels();
builder.Services.AddScoped<MangaJaNaiUpscaler>();

builder.Services.AddLocalization();

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
    options.UseSqlite(
        connectionString,
        sqlite =>
        {
            sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        }
    )
);
builder.Services.AddDbContext<ApplicationDbContext>(
    options =>
        options.UseSqlite(
            connectionString,
            sqlite =>
            {
                sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            }
        ),
    optionsLifetime: ServiceLifetime.Singleton
);
builder.Services.AddDbContext<LoggingDbContext>(options =>
    options.UseSqlite(
        loggingConnectionReadOnlyString,
        builder =>
        {
            builder.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        }
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

var app = builder.Build();

app.UseForwardedHeaders();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Create database backup before .NET 10 upgrade if this is the first time
    var dbPath = Path.GetFullPath(sqliteConnectionStringBuilder.DataSource);
    string dbDirectory =
        Path.GetDirectoryName(dbPath)
        ?? throw new InvalidOperationException("Unable to determine database directory");
    var upgradeMarkerFile = Path.Combine(dbDirectory, ".net10-upgrade-complete");

    if (!File.Exists(upgradeMarkerFile) && File.Exists(dbPath))
    {
        try
        {
            var backupPath = dbPath + ".bak";
            File.Copy(dbPath, backupPath, overwrite: false);
            logger.LogInformation(
                "Created database backup at {BackupPath} before .NET 10 upgrade",
                backupPath
            );

            // Also backup the logging database if it exists
            var loggingDbPath = Path.GetFullPath(loggingConnectionReadOnlyStringBuilder.DataSource);
            if (File.Exists(loggingDbPath))
            {
                var loggingBackupPath = loggingDbPath + ".bak";
                File.Copy(loggingDbPath, loggingBackupPath, overwrite: false);
                logger.LogInformation(
                    "Created logging database backup at {BackupPath}",
                    loggingBackupPath
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to create database backup before .NET 10 upgrade. Continuing with migration..."
            );
        }
    }

    try
    {
        dbContext.Database.Migrate();
        logger.LogDebug("Database migrations applied successfully.");

        // Mark the .NET 10 upgrade as complete
        try
        {
            await File.WriteAllTextAsync(
                upgradeMarkerFile,
                $"Upgrade completed on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"
            );
            logger.LogDebug("Marked .NET 10 upgrade as complete");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to create upgrade marker file, but migration completed successfully"
            );
        }

        if (app.Environment.IsProduction())
        {
            // A quick check to see if vacuum is needed could go here (e.g. checking file size)
            await dbContext.Database.ExecuteSqlRawAsync("VACUUM;");
            logger.LogInformation("Database vacuumed successfully.");
        }

        // reset any tasks that were "Processing" (e.g. during a crash) back to "Pending"
        await dbContext
            .PersistedTasks.Where(task => task.Status == PersistedTaskStatus.Processing)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(p => p.Status, p => PersistedTaskStatus.Pending)
            );

        // Validate and upgrade existing merged chapter records for backward compatibility
        try
        {
            var backwardCompatibilityService =
                scope.ServiceProvider.GetRequiredService<IBackwardCompatibilityService>();
            await backwardCompatibilityService.ValidateAndUpgradeExistingRecordsAsync();
            logger.LogDebug("Backward compatibility validation completed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Backward compatibility validation failed, but application will continue. Some merge functionality may be affected for existing records."
            );
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, $"An error occurred while applying migrations: {ex.Message}");
        // Log or handle the exception as appropriate for your app
    }

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

var supportedCultures = new[] { "en-US", "de-DE", "ja-JP", "de", "ja" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

localizationOptions.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(async context =>
{
    var user = context.User;
    if (user.Identity?.IsAuthenticated == true)
    {
        var localeClaim = user.FindFirst("locale");
        if (localeClaim != null)
        {
            return new ProviderCultureResult(localeClaim.Value);
        }
    }
    return await Task.FromResult<ProviderCultureResult?>(null);
}));

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
