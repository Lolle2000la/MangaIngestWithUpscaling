using System.Globalization;
using System.Security.Claims;
using MangaIngestWithUpscaling.Api;
using MangaIngestWithUpscaling.Api.Auth;
using MangaIngestWithUpscaling.Components;
using MangaIngestWithUpscaling.Components.Account;
using MangaIngestWithUpscaling.Configuration;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.Postgres;
using MangaIngestWithUpscaling.Data.Sqlite;
using MangaIngestWithUpscaling.Services;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.TaskDescribers;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using MudBlazor.Translations;
using Npgsql;
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

DatabaseProvider databaseProvider = DatabaseProviderResolver.Resolve(
    builder.Configuration.GetValue<string>("DatabaseProvider")
);

bool isSqlite = databaseProvider == DatabaseProvider.Sqlite;

// Each provider's connection string is only required when that provider is selected, so a
// PostgreSQL deployment does not need DefaultConnection and vice versa (the shipped
// appsettings.json provides both defaults).
string sqliteConnectionString = isSqlite
    ? builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' not found while DatabaseProvider is 'Sqlite'."
        )
    : string.Empty;

string postgresConnectionString = isSqlite
    ? string.Empty
    : builder.Configuration.GetConnectionString("PostgresConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'PostgresConnection' not found while DatabaseProvider is 'Postgres'."
        );

string applicationConnectionString = isSqlite ? sqliteConnectionString : postgresConnectionString;
string applicationMigrationsAssembly = isSqlite
    ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
    : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!;

SqliteConnectionStringBuilder? sqliteConnectionStringBuilder = isSqlite
    ? new SqliteConnectionStringBuilder(sqliteConnectionString)
    : null;

// The logs database is only a local SQLite file; on PostgreSQL the logs live in the application
// database, so this connection string is neither read nor parsed for PostgreSQL (a deployment may
// repurpose LoggingConnection, and parsing it as SQLite would be meaningless).
string? loggingConnectionReadOnlyString = null;
var logsDbPath = string.Empty;
if (isSqlite)
{
    var loggingConnectionString =
        builder.Configuration.GetConnectionString("LoggingConnection") ?? "Data Source=logs.db";
    var loggingConnectionReadOnlyStringBuilder = new SqliteConnectionStringBuilder(
        loggingConnectionString
    );
    loggingConnectionReadOnlyString = loggingConnectionReadOnlyStringBuilder.ConnectionString;
    logsDbPath = Path.GetFullPath(loggingConnectionReadOnlyStringBuilder.DataSource);
}

// Set WAL before app startup. This is idempotent and safe for existing databases.
// Do it before builder.Build() so logs.db is configured before Serilog opens it.
// For the logs database, also detect corruption and move the file aside so a fresh
// one can be created instead of crashing the application on startup.
var earlyDbPaths = new List<string>();
if (isSqlite)
{
    earlyDbPaths.Add(Path.GetFullPath(sqliteConnectionStringBuilder!.DataSource));
    earlyDbPaths.Add(logsDbPath);
}

foreach (var earlyDbPath in earlyDbPaths)
{
    var isLogsDb = earlyDbPath == logsDbPath;
    try
    {
        using var earlyConn = new SqliteConnection($"Data Source={earlyDbPath}");
        earlyConn.Open();
        using var integrityCmd = earlyConn.CreateCommand();
        integrityCmd.CommandText = "PRAGMA integrity_check";
        var integrityResult = integrityCmd.ExecuteScalar() as string;
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            if (isLogsDb)
            {
                MoveCorruptDatabaseAside(earlyDbPath);
                continue;
            }

            throw new IOException($"Database integrity check failed: {integrityResult}");
        }

        using var earlyCmd = earlyConn.CreateCommand();
        earlyCmd.CommandText = "PRAGMA journal_mode=WAL";
        earlyCmd.ExecuteScalar();
    }
    catch when (isLogsDb)
    {
        MoveCorruptDatabaseAside(earlyDbPath);
    }
    catch
    {
        // Non-critical: WAL mode will be re-verified with logging after migrations
    }
}

// On PostgreSQL the logs live in the application database. The Serilog sink does not create the
// table for us (needAutoCreateTable is disabled so the schema stays under our control), so ensure
// it exists before the sink starts writing.
if (!isSqlite)
{
    // The DDL is IF NOT EXISTS, but concurrent DDL is not fully atomic: replicas starting together
    // can each observe a transient 23505/42P07/42710 (the other replica created the object first)
    // or 40P01 (deadlock), and an operator-configured lock_timeout can surface as 55P03 during the
    // same contention. Retry briefly so the loser converges instead of leaving the sink
    // (needAutoCreateTable: false) without a table to write to. Runs before the advisory-locked
    // migration deliberately, so the table exists the moment the sink starts.
    const int maxAttempts = 5;
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using var logsConnection = new NpgsqlConnection(postgresConnectionString);
            logsConnection.Open();
            using var createLogsCommand = logsConnection.CreateCommand();
            createLogsCommand.CommandText = PostgresLogging.CreateTableSql;
            createLogsCommand.ExecuteNonQuery();
            break;
        }
        catch (PostgresException ex)
            when (IsRetryableLogsBootstrapFailure(ex) && attempt < maxAttempts)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
        }
        catch (Exception ex)
        {
            // Best effort: this runs before the host (and its logger) exists, so stderr is the only
            // channel. A genuinely unusable database fails the migration below anyway; a missing Logs
            // table alone means the sink (needAutoCreateTable: false) drops writes, so log the detail.
            Console.Error.WriteLine($"Failed to ensure the PostgreSQL Logs table exists: {ex}");
            break;
        }
    }
}

// Transient failures a concurrent replica's CREATE TABLE/INDEX can cause even with IF NOT EXISTS:
// the object is created by another session between our catalog check and our DDL.
static bool IsRetryableLogsBootstrapFailure(PostgresException ex) =>
    ex.SqlState
        is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.DuplicateTable
            or PostgresErrorCodes.DuplicateObject
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.LockNotAvailable;

static void MoveCorruptDatabaseAside(string dbPath)
{
    try
    {
        if (File.Exists(dbPath))
        {
            var timestamp = DateTime.UtcNow.ToString(
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture
            );
            var backupPath = $"{dbPath}.corrupted.{timestamp}";
            File.Move(dbPath, backupPath);
        }

        var walPath = dbPath + "-wal";
        var shmPath = dbPath + "-shm";
        try
        {
            if (File.Exists(walPath))
                File.Delete(walPath);
        }
        catch { }

        try
        {
            if (File.Exists(shmPath))
                File.Delete(shmPath);
        }
        catch { }
    }
    catch
    {
        // Best effort: if we can't move/delete the files, the startup will
        // proceed normally and may fail with the original error
    }
}

// Applies the PostgreSQL migrations under a session-level advisory lock. Several replicas of the
// app may start at the same time against the same database; without serialization they race on the
// shared __EFMigrationsHistory table (duplicate inserts, "relation already exists", deadlocks) and,
// because a failed PostgreSQL migration now fails fast, the losers would crash-loop. The lock is
// held on a dedicated connection so it survives independently of EF's own connection handling for
// the whole migration, and it is released (or auto-released when the connection closes) in a
// finally so it cannot mask a migration error.
static void MigratePostgresWithAdvisoryLock(
    ApplicationDbContext dbContext,
    Microsoft.Extensions.Logging.ILogger logger
)
{
    // Fixed, application-specific advisory-lock key ("MangaIU" plus a version byte). Both the lock
    // and the unlock must use the same value; it must not change between releases or replicas of
    // different versions would stop serializing with each other.
    const long migrationLockKey = 0x4D616E6761495500L;

    using var lockConnection = new NpgsqlConnection(dbContext.Database.GetConnectionString());
    lockConnection.Open();

    using (var lockCommand = lockConnection.CreateCommand())
    {
        lockCommand.CommandText = "SELECT pg_advisory_lock(@key)";
        lockCommand.Parameters.AddWithValue("key", migrationLockKey);
        lockCommand.ExecuteNonQuery();
    }

    try
    {
        dbContext.Database.Migrate();
    }
    finally
    {
        try
        {
            using var unlockCommand = lockConnection.CreateCommand();
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(@key)";
            unlockCommand.Parameters.AddWithValue("key", migrationLockKey);
            unlockCommand.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Never let a failed unlock hide the real migration result.
            logger.LogDebug(ex, "Failed to release the PostgreSQL migration advisory lock");
        }
    }
}

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

        if (isSqlite)
        {
            lc.WriteTo.SQLite(
                logsDbPath,
                tableName: "Logs",
                // Persist UTC so the stored instant matches PostgreSQL and the logs UI, which reads
                // the value as Unspecified and calls ToLocalTime(); local wall-clock would shift.
                storeTimestampInUtc: true,
                retentionPeriod: TimeSpan.FromDays(7),
                maxDatabaseSize: 100,
                rollOver: false
            );
        }
        else
        {
            lc.WriteTo.PostgreSQL(
                postgresConnectionString,
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
        databaseProvider,
        applicationConnectionString,
        applicationMigrationsAssembly
    )
);
builder.Services.AddDbContext<ApplicationDbContext>(
    options =>
        DatabaseSetup.UseDatabaseProvider(
            options,
            databaseProvider,
            applicationConnectionString,
            applicationMigrationsAssembly
        ),
    optionsLifetime: ServiceLifetime.Singleton
);
builder.Services.AddDbContext<LoggingDbContext>(options =>
    DatabaseSetup.UseDatabaseProvider(
        options,
        databaseProvider,
        isSqlite ? loggingConnectionReadOnlyString! : postgresConnectionString,
        isSqlite
            ? typeof(SqliteMigrationsAssemblyMarker).Assembly.FullName!
            : typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName!
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
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // The .NET 10 upgrade backup and marker only apply to the SQLite database file.
    string? upgradeMarkerFile = null;
    if (isSqlite)
    {
        var dbPath = Path.GetFullPath(sqliteConnectionStringBuilder!.DataSource);
        string dbDirectory =
            Path.GetDirectoryName(dbPath)
            ?? throw new InvalidOperationException("Unable to determine database directory");
        upgradeMarkerFile = Path.Combine(dbDirectory, ".net10-upgrade-complete");

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
                var loggingDbPath = logsDbPath;
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
    }

    try
    {
        if (isSqlite)
        {
            dbContext.Database.Migrate();
        }
        else
        {
            MigratePostgresWithAdvisoryLock(dbContext, logger);
        }

        logger.LogDebug("Database migrations applied successfully.");

        if (isSqlite && upgradeMarkerFile is not null)
        {
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
        }

        // Re-assert and verify WAL mode for the main database (SQLite only).
        // SQLite returns the active journal mode, so check the returned value.
        if (isSqlite)
        {
            try
            {
                dbContext.Database.OpenConnection();
                var mainConn = dbContext.Database.GetDbConnection();
                using var walCmd = mainConn.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode=WAL";
                var mode = (string?)await walCmd.ExecuteScalarAsync() ?? "unknown";
                if (mode == "wal")
                    logger.LogDebug("WAL journal mode enabled for main database.");
                else
                    logger.LogWarning(
                        "WAL journal mode could not be enabled for the main database (current mode: {Mode}). "
                            + "The database may be more susceptible to corruption on unexpected shutdowns.",
                        mode
                    );
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to enable WAL journal mode for the main database. "
                        + "The database may be more susceptible to corruption on unexpected shutdowns."
                );
            }
            finally
            {
                dbContext.Database.CloseConnection();
            }
        }

        // Best-effort: re-apply WAL mode for the logging database. Only relevant when logs are
        // stored in a local SQLite file.
        if (isSqlite)
        {
            try
            {
                var loggingDbContext = scope.ServiceProvider.GetRequiredService<LoggingDbContext>();
                loggingDbContext.Database.OpenConnection();
                try
                {
                    var loggingConn = loggingDbContext.Database.GetDbConnection();
                    using var walCmd = loggingConn.CreateCommand();
                    walCmd.CommandText = "PRAGMA journal_mode=WAL";
                    var mode = (string?)await walCmd.ExecuteScalarAsync() ?? "unknown";
                    if (mode == "wal")
                        logger.LogDebug("WAL journal mode enabled for logging database.");
                    else
                        logger.LogWarning(
                            "WAL journal mode could not be enabled for the logging database (current mode: {Mode}).",
                            mode
                        );
                }
                finally
                {
                    loggingDbContext.Database.CloseConnection();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enable WAL journal mode for logging database.");
            }
        }

        if (app.Environment.IsProduction())
        {
            if (isSqlite)
            {
                // A quick check to see if vacuum is needed could go here (e.g. checking file size)
                await dbContext.Database.ExecuteSqlRawAsync("VACUUM;");
                logger.LogInformation("Database vacuumed successfully.");
            }

            // Also vacuum the logging database to reclaim space from truncated logs (SQLite only).
            if (isSqlite)
            {
                try
                {
                    var loggingDbContext =
                        scope.ServiceProvider.GetRequiredService<LoggingDbContext>();
                    await loggingDbContext.Database.ExecuteSqlRawAsync("VACUUM;");
                    logger.LogInformation("Logging database vacuumed successfully.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to vacuum logging database.");
                }
            }
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

        if (!isSqlite)
        {
            // An external database that failed to migrate means every later query runs against an
            // unknown schema. Fail fast so the orchestrator restarts the app once the database is
            // reachable; the previous tolerant behavior is kept for the local SQLite file.
            throw;
        }
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
