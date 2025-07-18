using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.RemoteWorker.Services;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Python;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.Options;
#if VELOPACK_RELEASE
using Velopack;
using Velopack.Sources;
#endif

// Configure the HTTP client factory to use HTTP/2 for unencrypted connections
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("Ingest_");

builder.RegisterConfig();

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
var githubSource = new GithubSource("https://github.com/Lolle2000la/MangaIngestWithUpscaling", null, false);
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

builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection(WorkerConfig.SectionName));

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<UpscalingService.UpscalingServiceClient>(o =>
{
    o.CallOptionsActions.Add(context =>
    {
        var config = context.ServiceProvider.GetRequiredService<IOptions<WorkerConfig>>().Value;
        Metadata metadata = context.CallOptions.Headers ?? new Metadata();
        metadata.Add("Authorization", $"ApiKey {config.ApiKey}");
        context.CallOptions = context.CallOptions.WithHeaders(metadata);
    });

    o.Address = new Uri(builder.Configuration["WorkerConfig:ApiUrl"]!);
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
    var client = scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var testResponse = client.CheckConnection(new Empty());
    logger.LogDebug("Connection test response: {Response}", testResponse);

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

        var environment = await pythonService.PreparePythonEnvironment(upscalerConfig.Value.PythonEnvironmentDirectory, upscalerConfig.Value.PreferredGpuBackend);
        PythonService.Environment = environment;

        logger.LogInformation($"Python environment prepared at {environment.PythonExecutablePath} with {environment.InstalledBackend} backend");
    }

    var upscaler = scope.ServiceProvider.GetRequiredService<IUpscaler>();
    await upscaler.DownloadModelsIfNecessary(CancellationToken.None);
}

app.Run();