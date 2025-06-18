using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Components;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using MudBlazor.Translations;
using Serilog;
using System.Data.SQLite;
using System.Security.Claims;

// Required for Forwarded Headers

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("Ingest_");

builder.RegisterConfig(); // Register the configuration classes

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
                          throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

SQLiteConnectionStringBuilder sqliteConnectionStringBuilder = new(connectionString);

var loggingConnectionString = builder.Configuration.GetConnectionString("LoggingConnection") ?? "Data Source=logs.db";

var loggingConnectionReadOnlyStringBuilder = new SQLiteConnectionStringBuilder(loggingConnectionString);
var loggingConnectionReadOnlyString = loggingConnectionReadOnlyStringBuilder.ConnectionString;

//Log.Logger = new LoggerConfiguration()
//    .ReadFrom.Configuration(builder.Configuration)
//    .CreateLogger();

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.SQLite(
        Path.GetFullPath(loggingConnectionReadOnlyStringBuilder.DataSource),
        tableName: "Logs",
        retentionPeriod: TimeSpan.FromDays(7)));

// Configure Forwarded Headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // If the proxy isn't on localhost from the app container's perspective
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddGrpc();


builder.Services.AddMudServices();
builder.Services.AddMudTranslations();
builder.Services.RegisterViewModels();

builder.Services.AddLocalization();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

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
    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme,
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
                OnTokenValidated = async ctx =>
                {
                    var signInManager = ctx.HttpContext.RequestServices
                        .GetRequiredService<SignInManager<ApplicationUser>>();
                    var userManager =
                        ctx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();

                    if (ctx.Principal == null)
                    {
                        ctx.Fail("Principal is null.");
                        return;
                    }

                    string? emailClaim = ctx.Principal.FindFirstValue(ClaimTypes.Email) ??
                                         ctx.Principal.FindFirstValue("email");
                    string? nameIdentifier = ctx.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (emailClaim != null && nameIdentifier != null)
                    {
                        ApplicationUser? user = await userManager.FindByEmailAsync(emailClaim);
                        if (user == null)
                        {
                            user = new ApplicationUser
                            {
                                UserName = emailClaim, Email = emailClaim, EmailConfirmed = true
                            };
                            IdentityResult createUserResult = await userManager.CreateAsync(user);
                            if (!createUserResult.Succeeded)
                            {
                                ctx.Fail(
                                    $"Failed to create user: {string.Join(", ", createUserResult.Errors.Select(e => e.Description))}");
                                return;
                            }
                        }

                        var externalLoginInfo = new UserLoginInfo(ctx.Scheme.Name, nameIdentifier, ctx.Scheme.Name);
                        IList<UserLoginInfo> logins = await userManager.GetLoginsAsync(user);
                        if (!logins.Any(l =>
                                l.LoginProvider == externalLoginInfo.LoginProvider &&
                                l.ProviderKey == externalLoginInfo.ProviderKey))
                        {
                            IdentityResult addLoginResult = await userManager.AddLoginAsync(user, externalLoginInfo);
                            if (!addLoginResult.Succeeded)
                            {
                                ctx.Fail(
                                    $"Failed to add OIDC login to user: {string.Join(", ", addLoginResult.Errors.Select(e => e.Description))}");
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
                }
            };
        });
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString, builder =>
    {
        builder.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }));
builder.Services.AddDbContext<LoggingDbContext>(options =>
    options.UseSqlite(loggingConnectionReadOnlyString, builder =>
    {
        builder.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;
        options.SignIn.RequireConfirmedPhoneNumber = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddDataProtection()
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
    var loggingDbContext = scope.ServiceProvider.GetRequiredService<LoggingDbContext>();
    try
    {
        dbContext.Database.Migrate();
        Console.WriteLine("Database migrations applied successfully.");

        //loggingDbContext.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"An error occurred while applying migrations: {ex.Message}");
        // Log or handle the exception as appropriate for your app
    }

    // Also initialize python environment
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var pythonService = scope.ServiceProvider.GetRequiredService<IPythonService>();
    var upscalerConfig = scope.ServiceProvider.GetRequiredService<IOptions<UpscalerConfig>>();
    if (!pythonService.IsPythonInstalled())
    {
        logger.LogError(
            "Python is not installed on the system. Please install Python 3.6 or newer and ensure it is available on the system PATH.");
    }
    else
    {
        logger.LogInformation("Python is installed on the system.");

        Directory.CreateDirectory(upscalerConfig.Value.PythonEnvironmentDirectory);

        var environment = await pythonService.PreparePythonEnvironment(upscalerConfig.Value.PythonEnvironmentDirectory);
        PythonService.Environment = environment;

        logger.LogInformation($"Python environment prepared at {environment.PythonExecutablePath}");
    }
}

app.UseRequestLocalization(new RequestLocalizationOptions()
    .AddSupportedCultures(new[] { "en-US", "de-DE", "ja" })
    .AddSupportedUICultures(new[] { "en-US", "de-DE", "ja" }));


app.UseAuthentication();
app.UseAuthorization();

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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
// Only map these if OIDC is NOT enabled, or map them conditionally
if (!app.Configuration.GetValue<bool>("OIDC:Enabled"))
{
    app.MapAdditionalIdentityEndpoints();
}

// Add OIDC Logout Endpoint if OIDC is enabled
if (app.Configuration.GetValue<bool>("OIDC:Enabled"))
{
    app.MapPost("/Account/LogoutOidc",
        async (HttpContext context, SignInManager<ApplicationUser> signInManager, string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
                new AuthenticationProperties { RedirectUri = returnUrl ?? "/" });
        }).RequireAuthorization();
}

app.Run();