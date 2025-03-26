using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Components;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services;
using MangaIngestWithUpscaling.Services.Python;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using MudBlazor.Translations;
using Serilog;
using System.Data.SQLite;


var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("Ingest_");

builder.RegisterConfig(); // Register the configuration classes

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

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

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null)
    .AddIdentityCookies();

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

// Add the services used by the app
builder.Services.RegisterAppServices();

var app = builder.Build();

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
        logger.LogError("Python is not installed on the system. Please install Python 3.6 or newer and ensure it is available on the system PATH.");
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapApiEndpoints();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();