using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;
using MangaIngestWithUpscaling.RemoteWorker.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    if (OperatingSystem.IsWindows())
    {
        serverOptions.ListenNamedPipe("MIWURemoteWorker");
    }
    else
    {
        var socketPath = Path.Combine(Path.GetTempPath(), "miwu-remote.tmp");
        serverOptions.ListenUnixSocket(socketPath);
    }

    serverOptions.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection(WorkerConfig.SectionName));

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<UpscalingService.UpscalingServiceClient>(o =>
{
    o.CallOptionsActions.Add(context =>
    {
        var config = context.ServiceProvider.GetRequiredService<IOptions<WorkerConfig>>().Value;
        context.CallOptions.Headers!.Add("X-Api-Key", config.ApiKey);
    });

    o.Address = new Uri(builder.Configuration["WorkerConfig:ApiUrl"]!);
});

builder.Services.RegisterRemoteWorkerServices();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<GreeterService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
