using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.RemoteWorker.Services;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;
using Serilog;
#if VELOPACK_RELEASE
using Velopack;
using Velopack.Sources;
#endif

// Configure the HTTP client factory to use HTTP/2 for unencrypted connections
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("Ingest_");

builder.RegisterConfig();

builder.Services.AddSerilog(
    (services, lc) =>
        lc
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
);

// Currently, no API is configured for the remote worker, so configuring Kestrel to listen on a named pipe or Unix socket is not necessary.
// builder.WebHost.ConfigureKestrel(serverOptions =>
// {
//     if (OperatingSystem.IsWindows())
//     {
//         serverOptions.ListenNamedPipe("MIWURemoteWorker");
//     }
//     else
//     {
//         var socketPath = Path.Combine(Path.GetTempPath(), "miwu-remote.tmp");
//         if (File.Exists(socketPath))
//         {
//             try
//             {
//                 File.Delete(socketPath);
//             }
//             catch (Exception ex)
//             {
//                 throw new InvalidOperationException($"Failed to delete existing socket file at {socketPath}.", ex);
//             }
//         }
//
//         serverOptions.ListenUnixSocket(socketPath);
//     }
//
//     serverOptions.ConfigureEndpointDefaults(listenOptions =>
//     {
//         listenOptions.Protocols = HttpProtocols.Http2;
//     });
// });

#if VELOPACK_RELEASE
VelopackApp.Build().Run();
var githubSource = new GithubSource(
    "https://github.com/Lolle2000la/MangaIngestWithUpscaling",
    null,
    false
);
var updateManager = new UpdateManager(githubSource);
var newVersion = await updateManager.CheckForUpdatesAsync();

if (newVersion != null)
{
    Console.WriteLine($"New version available: {newVersion.TargetFullRelease.Version}");
    Console.WriteLine($"Release notes: {newVersion.TargetFullRelease.NotesMarkdown}");

    await updateManager.DownloadUpdatesAsync(newVersion);

    // install new version and restart app
    updateManager.ApplyUpdatesAndRestart(newVersion);
}
else
{
    Console.WriteLine("No updates available.");
}
#endif

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddHealthChecks();

// Read config once for client configuration to avoid per-call ServiceProvider usage (prevents disposed-service issues)
var boundWorkerConfig = new WorkerConfig();
builder.Configuration.GetSection(WorkerConfig.SectionName).Bind(boundWorkerConfig);
string apiUrl = boundWorkerConfig.ApiUrl ?? builder.Configuration["WorkerConfig:ApiUrl"]!;
string authHeaderValue = $"ApiKey {boundWorkerConfig.ApiKey}";

builder.Services.AddGrpcClient<UpscalingService.UpscalingServiceClient>(o =>
{
    // Match the server's per-message receive ceiling (src/MangaIngestWithUpscaling/Program.cs); the
    // 4 MiB gRPC default would reject a manifest for a very large chapter (~40k pages).
    o.ChannelOptionsActions.Add(channelOptions =>
        channelOptions.MaxReceiveMessageSize = 32 * 1024 * 1024
    );

    o.CallOptionsActions.Add(context =>
    {
        Metadata metadata = context.CallOptions.Headers ?? new Metadata();
        metadata.Add("Authorization", authHeaderValue);
        context.CallOptions = context.CallOptions.WithHeaders(metadata);
    });

    o.Address = new Uri(apiUrl);
});

builder.Services.RegisterRemoteWorkerServices();

var app = builder.Build();

// Uncomment if the remote worker should at some point expose an API for configuration or status.
// // Configure the HTTP request pipeline.
// app.MapGet("/",
//     () =>
//         "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

using (var scope = app.Services.CreateScope())
{
    var client =
        scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    CheckConnectionResponse? connection = null;
    while (connection is null)
    {
        try
        {
            connection = client.CheckConnection(
                new CheckConnectionRequest
                {
                    ProtocolVersion = UpscalingProtocolVersion.Current,
                    MinSupportedProtocolVersion = UpscalingProtocolVersion.MinSupported,
                },
                deadline: DateTime.UtcNow.AddSeconds(5)
            );
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            // The server rejected our version outright; retrying cannot help.
            logger.LogError("The server rejected this worker: {Detail}", ex.Status.Detail);
            throw;
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "Failed to connect to server: {Message}. Retrying in 5 seconds...",
                ex.Status.Detail
            );
            await Task.Delay(5000);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to connect to server: {Message}. Retrying in 5 seconds...",
                ex.Message
            );
            await Task.Delay(5000);
        }
    }

    // Fail fast on version skew: an unsupported server would otherwise surface as opaque
    // Unimplemented failures for every task, dropping spools and burning retries. The server performs
    // the mirror-image check on the version we sent above.
    if (
        !UpscalingProtocolVersion.IsCompatible(
            connection.ProtocolVersion,
            connection.MinSupportedProtocolVersion
        )
    )
    {
        logger.LogError(
            "The server speaks upscaling protocol version {ServerVersion} (supports {ServerMin}-{ServerVersion}), but this worker (version {WorkerVersion}) supports {Min}-{Max}. Upgrade the worker and server together.",
            connection.ProtocolVersion,
            connection.MinSupportedProtocolVersion,
            connection.ProtocolVersion,
            UpscalingProtocolVersion.Current,
            UpscalingProtocolVersion.MinSupported,
            UpscalingProtocolVersion.Current
        );
        throw new InvalidOperationException(
            $"Incompatible upscaling protocol version {connection.ProtocolVersion}; this worker supports {UpscalingProtocolVersion.MinSupported}-{UpscalingProtocolVersion.Current}."
        );
    }

    logger.LogDebug("Connection test response: {Response}", connection);

    var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();
    await upscaler.DownloadModelsIfNecessary(CancellationToken.None);

    // Fail fast if the native preprocessing backend is broken, so the worker cannot silently produce
    // un-preprocessed pages while advertising the same engine identity as a healthy one.
    scope
        .ServiceProvider.GetRequiredService<MangaIngestWithUpscaling.Shared.Services.ImageProcessing.IImageResizeService>()
        .VerifyReady();
}

app.MapHealthChecks("/health");

app.Run();
